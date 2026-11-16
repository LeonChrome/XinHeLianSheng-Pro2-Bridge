using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Y700Switch2V60Viiper;

public sealed record NintendoNfcExportResult(int Bytes,string Sha256,bool SignatureIncluded,bool MetadataSaved,string MetadataError);

public static class NintendoNfcExport
{
    public static NintendoNfcExportResult Save(string path,NintendoNfcTag tag,bool includeSignature,bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] raw=tag.Data.ToArray();
        if(!Pro1NfcProtocol.IsNtag215(raw,tag.Uid))
            throw new InvalidDataException("标签长度或 UID/BCC 校验失败，未保存文件。");
        if(includeSignature&&tag.Signature.Length!=32)
            throw new InvalidDataException("没有完整的 32 字节签名字段，未保存文件。");
        byte[] payload=raw;
        if(includeSignature)
        {
            payload=new byte[572];raw.CopyTo(payload,0);tag.Signature.CopyTo(payload,540);
        }
        string fullPath=Path.GetFullPath(path);
        string sha256=Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        WriteVerified(fullPath,payload,overwrite);
        // The binary is the deliverable. An optional sidecar failure must not
        // misreport an already verified binary as missing or corrupt.
        bool metadataSaved=false;string metadataError="";
        try
        {
            byte[] metadata=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schema=1,uid=Convert.ToHexString(tag.Uid),length=payload.Length,sha256,
                tag.Controller,tag.Transport,tag.ReadAt,signatureIncluded=includeSignature,
                authenticationVerified=false
            },new JsonSerializerOptions{WriteIndented=true}));
            WriteVerified(fullPath+".json",metadata,overwrite);
            metadataSaved=true;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException)
        {metadataError=error.GetType().Name+" (0x"+error.HResult.ToString("X8")+")";}
        return new(payload.Length,sha256,includeSignature,metadataSaved,metadataError);
    }

    private static void WriteVerified(string destination,byte[] data,bool overwrite)
    {
        string parent=Path.GetDirectoryName(destination)!;
        string temporary=Path.Combine(parent,"."+Path.GetFileName(destination)+"."+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {stream.Write(data);stream.Flush(flushToDisk:true);}
            if(!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(data))throw new IOException("临时文件写入校验失败。");
            File.Move(temporary,destination,overwrite);
            if(!File.ReadAllBytes(destination).AsSpan().SequenceEqual(data))throw new IOException("保存后的文件校验失败。");
        }
        finally
        {
            try{if(File.Exists(temporary))File.Delete(temporary);}
            catch(IOException){}
            catch(UnauthorizedAccessException){}
        }
    }
}
