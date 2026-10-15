using System.Diagnostics;
using System.Text.Json;
using Ava.Infrastructure;
using SherpaOnnx;
if(args.Length<1) { Console.WriteLine("Ava.Benchmark <model-folder> [16k-mono-pcm16.wav] | --download <model-folder>"); return; }
if(args[0]=="--download") { await ModelDownload.DownloadAsync(args[1],new Progress<double>(p=>Console.WriteLine($"{p:P1}"))); return; }
if(args.Length<2)throw new ArgumentException("Supply WAV path.");
using var reader=new BinaryReader(File.OpenRead(args[1]));
if(new string(reader.ReadChars(4))!="RIFF")throw new ArgumentException("Expected WAV.");
reader.ReadInt32(); if(new string(reader.ReadChars(4))!="WAVE")throw new ArgumentException("Expected WAVE.");
byte[]? data=null; int rate=0;short bits=0,channels=0,format=0;
while(reader.BaseStream.Position+8<=reader.BaseStream.Length) {
 var id=new string(reader.ReadChars(4)); int size=reader.ReadInt32(); if(size<0||size>reader.BaseStream.Length-reader.BaseStream.Position)throw new ArgumentException("Bad WAV chunk."); long end=reader.BaseStream.Position+size;
 if(id=="fmt ") { format=reader.ReadInt16(); channels=reader.ReadInt16();rate=reader.ReadInt32();reader.ReadInt32();reader.ReadInt16();bits=reader.ReadInt16(); }
 if(id=="data")data=reader.ReadBytes(size);
 reader.BaseStream.Position=end+(size%2);
}
if(format!=1||bits!=16||channels!=1||rate!=16000||data==null)throw new ArgumentException("Expected PCM16 mono 16kHz WAV.");
float[] samples=Enumerable.Range(0,data.Length/2).Select(i=>BitConverter.ToInt16(data,2*i)/32768f).ToArray();
foreach(int threads in new[]{2,4}) {
 var c=new OnlineRecognizerConfig();c.FeatConfig.SampleRate=16000;c.FeatConfig.FeatureDim=80;
 c.ModelConfig.Transducer.Encoder=Path.Combine(args[0],"encoder.int8.onnx");c.ModelConfig.Transducer.Decoder=Path.Combine(args[0],"decoder.int8.onnx");c.ModelConfig.Transducer.Joiner=Path.Combine(args[0],"joiner.int8.onnx");c.ModelConfig.Tokens=Path.Combine(args[0],"tokens.txt");c.ModelConfig.Provider="cpu";c.ModelConfig.NumThreads=threads;c.DecodingMethod="greedy_search";c.EnableEndpoint=0;
 var clock=Stopwatch.StartNew(); using var recognizer=new OnlineRecognizer(c); double load=clock.Elapsed.TotalMilliseconds;
 for(int run=0;run<3;run++) { using var stream=recognizer.CreateStream();clock.Restart();
 for(int i=0;i<samples.Length;i+=8960) { var chunk=new float[8960];Array.Copy(samples,i,chunk,0,Math.Min(8960,samples.Length-i));stream.AcceptWaveform(16000,chunk);while(recognizer.IsReady(stream))recognizer.Decode(stream); }
 double feeding=clock.Elapsed.TotalMilliseconds;stream.AcceptWaveform(16000,new float[16000]);stream.InputFinished();while(recognizer.IsReady(stream))recognizer.Decode(stream);
 Console.WriteLine(JsonSerializer.Serialize(new {platform=Environment.OSVersion.ToString(),threads,run,loadMs=load,audioSeconds=samples.Length/16000d,decodeMs=clock.Elapsed.TotalMilliseconds,finalizeMs=clock.Elapsed.TotalMilliseconds-feeding,workingSetBytes=Process.GetCurrentProcess().WorkingSet64,text=recognizer.GetResult(stream).Text})); }
}
