using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace Y700Switch2V60Viiper;

public static class WirelessRelayRuntime
{
    private const string ResourceName="Embedded.wireless.runtime.zip";
    private static readonly object extractionGate=new();
    public static bool IsAvailable=>typeof(WirelessRelayRuntime).Assembly.GetManifestResourceNames().Contains(ResourceName);
#if EMBED_UPSTREAM_REALTEK
    public static bool HasUpstreamRealtek=>true;
#else
    public static bool HasUpstreamRealtek=>false;
#endif

    // The single EXE carries the backend, firmware, helper and original notices.
    // Extraction is file-only. It never opens a radio or installs a driver.
    public static string EnsureExtracted(string? verificationDirectory=null)
    {
        lock(extractionGate)
        {
            using var resource=typeof(WirelessRelayRuntime).Assembly.GetManifestResourceStream(ResourceName)
                ??throw new IOException("此构建未包含蓝牙转发组件。");
            using var memory=new MemoryStream();resource.CopyTo(memory);
            string hash=Convert.ToHexString(SHA256.HashData(memory.ToArray())).ToLowerInvariant();memory.Position=0;
            string root=Path.GetFullPath(verificationDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PRO2WirelessReceiverControlBoard","embedded","wireless-relay",hash[..16]));
            CreatePrivate(root);
            using var archive=new ZipArchive(memory,ZipArchiveMode.Read);
            var manifestEntry=archive.GetEntry("runtime-manifest.json")??throw new IOException("转发组件清单缺失。");
            using var manifestStream=manifestEntry.Open();
            var manifest=JsonSerializer.Deserialize<Dictionary<string,string>>(manifestStream)??throw new IOException("转发组件清单无效。");
            if(manifest.Count is <3 or >40||archive.Entries.Count!=manifest.Count+1)throw new IOException("转发组件数量不符。");
            foreach(string required in new[]{"pro2-wireless-pro1-radio.exe","RadioDriver.ps1","BTstack-LICENSE.txt","firmware/rtl8852cu_fw"})
                if(!manifest.ContainsKey(required))throw new IOException("转发组件缺失："+required);
            foreach(var file in manifest)
            {
                string relative=file.Key.Replace('\\','/');
                if(relative!=file.Key||relative.StartsWith('/')||relative.Split('/').Any(p=>p is "" or "." or "..")||relative.Contains(':'))
                    throw new IOException("转发组件路径无效。");
                var entry=archive.GetEntry(relative)??throw new IOException("转发组件未找到："+relative);
                if(entry.Length>4*1024*1024)throw new IOException("转发组件大小异常。");
                byte[] data;using(var stream=entry.Open()){using var buffer=new MemoryStream();stream.CopyTo(buffer);data=buffer.ToArray();}
                if(!Convert.ToHexString(SHA256.HashData(data)).Equals(file.Value,StringComparison.OrdinalIgnoreCase))throw new IOException("转发组件哈希不符："+relative);
                string destination=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));
                RejectReparseParents(destination,root);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if(File.Exists(destination)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))).Equals(file.Value,StringComparison.OrdinalIgnoreCase))continue;
                string temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{File.WriteAllBytes(temporary,data);File.Move(temporary,destination,true);}
                finally{if(File.Exists(temporary))File.Delete(temporary);}
            }
            return root;
        }
    }
    private static void CreatePrivate(string root)
    {
        RejectReparseParents(root,Path.GetPathRoot(root)!);
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        using var identity=WindowsIdentity.GetCurrent();
        if(!Directory.Exists(root))
        {
            var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new(identity.User!,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            new DirectoryInfo(root).Create(security);
        }
        var acl=new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access);
        if(!acl.AreAccessRulesProtected||acl.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Any(r=>r.AccessControlType==AccessControlType.Allow&&!r.IdentityReference.Equals(identity.User)))
            throw new UnauthorizedAccessException("蓝牙转发缓存目录权限异常，未启动发射器。");
    }
    private static void RejectReparseParents(string path,string stop)
    {
        string fullStop=Path.GetFullPath(stop).TrimEnd(Path.DirectorySeparatorChar);
        for(string? cursor=Path.GetFullPath(path);cursor!=null;cursor=Path.GetDirectoryName(cursor))
        {
            if((File.Exists(cursor)||Directory.Exists(cursor))&&(File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("蓝牙转发组件路径不能使用重定向链接。");
            if(cursor.TrimEnd(Path.DirectorySeparatorChar).Equals(fullStop,StringComparison.OrdinalIgnoreCase))break;
        }
    }
}
