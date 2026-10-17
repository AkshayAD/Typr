using Typr.Infrastructure;
using NAudio.Wave;
using Xunit;
namespace Typr.Infrastructure.Tests;
public class AudioTests
{
 [Fact] public void FloatStereoResamplingPreservesConstantSignalAcrossChunks() {
  var converter=new MicrophoneConverter(WaveFormat.CreateIeeeFloatWaveFormat(48000,2));
  var block=new byte[480*8];for(int i=0;i<960;i++)BitConverter.GetBytes(0.25f).CopyTo(block,i*4);
  var result=Enumerable.Range(0,100).SelectMany(_=>converter.Convert(block,block.Length)).ToArray();
  Assert.InRange(result.Length,15999,16000); Assert.All(result.Skip(128),x=>Assert.Equal(0.25f,x,5));
 }
 [Fact] public void StereoChannelsAreAveraged() {
  var converter=new MicrophoneConverter(new WaveFormat(16000,16,2));
  var block=new byte[400];for(int i=0;i<100;i++){BitConverter.GetBytes((short)16384).CopyTo(block,i*4);BitConverter.GetBytes((short)-16384).CopyTo(block,i*4+2);}
  Assert.All(converter.Convert(block,block.Length),x=>Assert.Equal(0,x));
 }
 [Fact] public void UnsupportedBitDepthFailsClearly() => Assert.Throws<NotSupportedException>(()=>new MicrophoneConverter(new WaveFormat(16000,8,1)));
 [Fact] public void FractionalResamplingIsInvariantToBlockBoundaries() {
  var format=WaveFormat.CreateIeeeFloatWaveFormat(44100,1);var a=new MicrophoneConverter(format);var b=new MicrophoneConverter(format);
  var block=new byte[44100*4];for(int i=0;i<44100;i++)BitConverter.GetBytes((float)Math.Sin(i*0.1)).CopyTo(block,i*4);
  var whole=a.Convert(block,block.Length);var chunks=new List<float>();for(int offset=0;offset<block.Length;offset+=588){var part=block.Skip(offset).Take(588).ToArray();chunks.AddRange(b.Convert(part,part.Length));}
  Assert.Equal(whole.Length,chunks.Count);for(int i=0;i<whole.Length;i++)Assert.InRange(Math.Abs(whole[i]-chunks[i]),0,0.0001f);
 }
 [Fact] public async Task MissingModelFailsIntegrityCheck() => Assert.False(await ModelDownload.VerifyAsync(Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString())));
 [Fact] public void MissingModelIsNotReady() => Assert.False(ModelDownload.IsReady(Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString())));
}
