using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
namespace Ava.Infrastructure;
public static class ModelDownload
{
    const string Repository="csukuangfj2/sherpa-onnx-nemotron-speech-streaming-en-0.6b-560ms-int8-2026-04-25";
    const string Revision="52056fdc070914a48dcd68b31b44d6a6f5b85902";
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Ava","models","nemotron-en-560ms");
    static readonly (string Name,long Size,string Hash)[] Files = [
        ("encoder.int8.onnx",652916849,"7d932213491ad355c6e5576705dc3494731a52af87d7a1b954559340147909d8"),
        ("decoder.int8.onnx",7257753,"0be9702c2f427a2b6bb241d298e0d3836a558de1f5b9fd3018f1cce6e2b3fa98"),
        ("joiner.int8.onnx",1735862,"a35eac38a22ebceb04d230ed7afe0d68f446ba6914a036b97f14fece95967e23"),
        ("tokens.txt",8952,"dc0b4584ab2e4ddbf888425c076c61b736e7356a015250db7d307e6f1a8188ff")];
    public static bool IsReady(string directory) => Files.All(f=>File.Exists(Path.Combine(directory,f.Name)) && new FileInfo(Path.Combine(directory,f.Name)).Length==f.Size);
    public static async Task<bool> VerifyAsync(string directory,CancellationToken cancellationToken=default)
    {
        foreach(var file in Files) if(!await Valid(Path.Combine(directory,file.Name),file.Size,file.Hash,cancellationToken)) return false;
        return true;
    }
    public static async Task DownloadAsync(string directory,IProgress<double>? progress=null,CancellationToken cancellationToken=default)
    {
        Directory.CreateDirectory(directory);
        using var client=new HttpClient { Timeout=Timeout.InfiniteTimeSpan };
        long done=0,total=Files.Sum(f=>f.Size);
        foreach(var file in Files) {
            string final=Path.Combine(directory,file.Name),part=final+".part";
            if(await Valid(final,file.Size,file.Hash,cancellationToken)) { done+=file.Size; progress?.Report((double)done/total); continue; }
            long offset=File.Exists(part)?new FileInfo(part).Length:0;
            if(offset>file.Size) { File.Delete(part); offset=0; }
            if(offset<file.Size) {
                using var request=new HttpRequestMessage(HttpMethod.Get,$"https://huggingface.co/{Repository}/resolve/{Revision}/{file.Name}");
                if(offset>0) request.Headers.Range=new RangeHeaderValue(offset,null);
                using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellationToken);
                response.EnsureSuccessStatusCode();
                if(offset>0 && response.StatusCode!=HttpStatusCode.PartialContent) offset=0;
                if(response.StatusCode==HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From!=offset) throw new IOException("Download resume position did not match.");
                await using var output=new FileStream(part,offset>0?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.None,81920,true);
                await using var input=await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer=new byte[81920]; int read;
                while((read=await input.ReadAsync(buffer,cancellationToken))>0) {
                    if(offset+read>file.Size) throw new IOException("Model file exceeded its expected size.");
                    await output.WriteAsync(buffer.AsMemory(0,read),cancellationToken); offset+=read; progress?.Report((double)(done+offset)/total);
                }
            }
            if(!await Valid(part,file.Size,file.Hash,cancellationToken)) { if(File.Exists(part)) File.Delete(part); throw new IOException($"Integrity check failed for {file.Name}; retry download."); }
            File.Move(part,final,true); done+=file.Size; progress?.Report((double)done/total);
        }
    }
    static async Task<bool> Valid(string path,long size,string hash,CancellationToken ct)
    {
        if(!File.Exists(path)||new FileInfo(path).Length!=size)return false;
        if(hash.Length==0)return true;
        await using var stream=File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream,ct)).Equals(hash,StringComparison.OrdinalIgnoreCase);
    }
}
