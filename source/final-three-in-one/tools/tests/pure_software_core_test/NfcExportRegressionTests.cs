using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class NfcExportRegressionTests
{
    public static void Run(Action<bool,string> check,string output)
    {
        string root=Path.Combine(output,"nfc-export-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        byte[] uid=[4,0x12,0x34,0x56,0x78,0x90,0xab];byte[] data=new byte[540];
        uid.AsSpan(0,3).CopyTo(data);uid.AsSpan(3).CopyTo(data.AsSpan(4));
        data[3]=(byte)(0x88^uid[0]^uid[1]^uid[2]);data[8]=(byte)(uid[3]^uid[4]^uid[5]^uid[6]);
        new byte[]{0xf1,0x10,0xff,0xee}.CopyTo(data,12);
        var tag=new NintendoNfcTag(uid,data,"test-only","Pro2Ble",DateTimeOffset.UnixEpoch)
            {Signature=Enumerable.Range(0,32).Select(i=>(byte)i).ToArray()};
        string rawPath=Path.Combine(root,"raw.bin");
        var raw=NintendoNfcExport.Save(rawPath,tag,false,false);
        check(raw.Bytes==540&&!raw.SignatureIncluded&&raw.MetadataSaved&&File.ReadAllBytes(rawPath).SequenceEqual(data),
            "default export writes exactly 540 original bytes with metadata");
        check(raw.Sha256==Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),"export digest matches bytes on disk");
        using(var json=JsonDocument.Parse(File.ReadAllText(rawPath+".json")))
        {
            check(json.RootElement.GetProperty("length").GetInt32()==540&&
                json.RootElement.GetProperty("sha256").GetString()==raw.Sha256&&
                !json.RootElement.GetProperty("authenticationVerified").GetBoolean(),
                "sidecar describes measured length/hash without claiming authentication");
        }
        string signedPath=Path.Combine(root,"signed.bin");
        var signed=NintendoNfcExport.Save(signedPath,tag,true,false);byte[] signedBytes=File.ReadAllBytes(signedPath);
        check(signed.Bytes==572&&signed.SignatureIncluded&&signedBytes[..540].SequenceEqual(data)&&
            signedBytes[540..].SequenceEqual(tag.Signature),"advanced export appends exactly the received signature field");
        File.WriteAllBytes(rawPath,[1,2,3]);
        try{NintendoNfcExport.Save(rawPath,tag,false,false);check(false,"overwrite must require caller consent");}
        catch(IOException){check(File.ReadAllBytes(rawPath).SequenceEqual(new byte[]{1,2,3}),"unapproved overwrite preserves existing destination");}
        NintendoNfcExport.Save(rawPath,tag,false,true);
        check(File.ReadAllBytes(rawPath).SequenceEqual(data),"approved replacement is verified after commit");
        string noSignaturePath=Path.Combine(root,"missing-signature.bin");
        try{NintendoNfcExport.Save(noSignaturePath,tag with{Signature=[]},true,false);check(false,"incomplete signature export");}
        catch(InvalidDataException){check(!File.Exists(noSignaturePath),"missing signature cannot create a misleading 572-byte file");}
        byte[] corrupt=(byte[])data.Clone();corrupt[3]^=1;
        try{NintendoNfcExport.Save(rawPath,tag with{Data=corrupt},false,true);check(false,"bad BCC export");}
        catch(InvalidDataException){check(File.ReadAllBytes(rawPath).SequenceEqual(data),"invalid tag cannot overwrite a previously good export");}
        string sidecarBlocked=Path.Combine(root,"sidecar-blocked.bin");Directory.CreateDirectory(sidecarBlocked+".json");
        var partial=NintendoNfcExport.Save(sidecarBlocked,tag,false,false);
        check(!partial.MetadataSaved&&partial.MetadataError.Length>0&&File.ReadAllBytes(sidecarBlocked).SequenceEqual(data),
            "sidecar failure reports a warning while retaining the verified binary");
        string blocked=Path.Combine(root,"directory-target.bin");Directory.CreateDirectory(blocked);
        try{NintendoNfcExport.Save(blocked,tag,false,true);check(false,"directory destination");}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException)
        {check(Directory.Exists(blocked),"failed commit does not remove destination directories");}
        check(!Directory.EnumerateFiles(root,"*.tmp").Any(),"success and failed commits leave no temporary exports");
        var recent=new RecentSessionStatus();recent.Add("[12:00:00.000] [NFC_EXPORT] bytes=540 verified=true sha256="+raw.Sha256);
        check(recent.Snapshot.Contains("[NFC_EXPORT]")&&recent.Snapshot.Contains(raw.Sha256),"export verification remains in recent diagnostics");
    }
}
