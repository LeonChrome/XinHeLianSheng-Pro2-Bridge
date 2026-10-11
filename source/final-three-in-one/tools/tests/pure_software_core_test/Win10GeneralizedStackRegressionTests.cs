using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Y700Switch2V60Viiper;

internal static class Win10GeneralizedStackRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        const string path = @"C:\Windows\System32\drivers\bthport.sys";
        byte[] Fixture(IEnumerable<string>? names = null, bool unicode = true)
        {
            byte[] data = new byte[2048];
            data[0] = (byte)'M'; data[1] = (byte)'Z';
            void Int(int offset, int value) => BitConverter.GetBytes(value).CopyTo(data, offset);
            void Short(int offset, ushort value) => BitConverter.GetBytes(value).CopyTo(data, offset);
            Int(0x3C, 0x80); Int(0x80, 0x00004550); Short(0x84, 0x8664);
            Short(0x86, 1); Short(0x94, 240); Short(0x96, 2);
            Short(0x98, 0x20B); Short(0x98 + 68, 1); Int(0x98 + 60, 512);
            Int(0x98 + 240 + 16, 1536); Int(0x98 + 240 + 20, 512);
            int at = 600;
            foreach (string name in names ?? Array.Empty<string>())
            {
                byte[] value = (unicode ? Encoding.Unicode : Encoding.ASCII).GetBytes(name + "\0");
                value.CopyTo(data, at); at += value.Length;
            }
            return data;
        }
        Win10StackParameterAssessment Inspect(int build, byte[] data, bool x64 = true,
            string? target = null) => Win10StackParameterAssessment.Analyze(build, x64, path,
                target ?? path, data, "10.0.19041.fixture");

        foreach (int build in new[] { 19041, 19042, 19043, 19044, 19045, 21999 })
        {
            var none = Inspect(build, Fixture());
            check(none.ValidSystemX64Image && none.CanExperiment && !none.IsAudited &&
                none.RequiresExperimentalConsent && none.EvidenceLevel == Win10StackEvidenceLevel.ExperimentalNoParameterNames,
                "unknown valid Win10 stack may experiment even with no parameter strings " + build);
            check(!none.CanApply(false) && none.CanApply(true),
                "unknown stack requires explicit consent, not a hardware/hash allowlist " + build);
            check(none.StackSha256.Length == 64 && none.StackVersion == "10.0.19041.fixture" &&
                none.ParameterNamesFound.Count == 0 && none.EvidenceSummary.Contains("不保证"),
                "unknown-stack assessment records hash/version and honest evidence limits " + build);
        }
        foreach (bool unicode in new[] { false, true })
        {
            var all = Inspect(19045, Fixture(Win10BleStackProfile.Values.Keys, unicode));
            check(all.ParameterNamesFound.Count == 4 &&
                all.EvidenceLevel == Win10StackEvidenceLevel.ExperimentalAllParameterNames &&
                !all.IsAudited && !all.CanApply(false) && all.CanApply(true),
                "all " + (unicode ? "Unicode" : "ASCII") + " strings are candidate evidence, not proof or automatic consent");
            var partial = Inspect(19045, Fixture(Win10BleStackProfile.Values.Keys.Take(2), unicode));
            check(partial.ParameterNamesFound.Count == 2 &&
                partial.EvidenceLevel == Win10StackEvidenceLevel.ExperimentalPartialParameterNames && partial.CanApply(true),
                "partial parameter strings do not create another hard compatibility gate");
            bool immutable = false;
            try { ((IList<string>)all.ParameterNamesFound)[0] = "fake"; }
            catch (NotSupportedException) { immutable = true; }
            check(immutable, "assessment name evidence is immutable");
        }
        foreach (int build in new[] { 7601, 18363, 19040, 22000, 26100, 26200, int.MaxValue })
        {
            var blocked = Inspect(build, Fixture());
            check(blocked.EvidenceLevel == Win10StackEvidenceLevel.Blocked &&
                !blocked.CanExperiment && !blocked.CanApply(true),
                "unsupported OS cannot bypass guard with experimental consent " + build);
        }
        check(!Inspect(19045, Fixture(), false).CanApply(true), "non-x64 platform rejects experiment");
        check(!Inspect(19045, Fixture(), target: @"D:\temp\bthport.sys").CanApply(true),
            "non-system driver path rejects experiment");
        check(Inspect(19045, Fixture(), target: path.ToUpperInvariant()).CanApply(true),
            "system path comparison is case-insensitive on Windows");
        foreach (int length in new[] { 0, 1, 63, 511 })
            check(!Inspect(19045, new byte[length]).CanApply(true), "short or missing binary rejects experiment " + length);
        var corruptions = new List<(string name, Action<byte[]> corrupt)>
        {
            ("DOS signature", d => d[0] = 0),
            ("negative PE offset", d => BitConverter.GetBytes(-1).CopyTo(d, 0x3C)),
            ("huge PE offset", d => BitConverter.GetBytes(int.MaxValue).CopyTo(d, 0x3C)),
            ("PE signature", d => d[0x80] = 0),
            ("machine x86", d => BitConverter.GetBytes((ushort)0x14C).CopyTo(d, 0x84)),
            ("no sections", d => BitConverter.GetBytes((ushort)0).CopyTo(d, 0x86)),
            ("too many sections", d => BitConverter.GetBytes((ushort)97).CopyTo(d, 0x86)),
            ("short optional header", d => BitConverter.GetBytes((ushort)32).CopyTo(d, 0x94)),
            ("overflow optional header", d => BitConverter.GetBytes(ushort.MaxValue).CopyTo(d, 0x94)),
            ("not executable", d => BitConverter.GetBytes((ushort)0).CopyTo(d, 0x96)),
            ("PE32 header", d => BitConverter.GetBytes((ushort)0x10B).CopyTo(d, 0x98)),
            ("GUI subsystem", d => BitConverter.GetBytes((ushort)2).CopyTo(d, 0x98 + 68)),
            ("headers overlap sections", d => BitConverter.GetBytes(200).CopyTo(d, 0x98 + 60)),
            ("headers beyond file", d => BitConverter.GetBytes(int.MaxValue).CopyTo(d, 0x98 + 60)),
            ("raw section beyond file", d => BitConverter.GetBytes(int.MaxValue).CopyTo(d, 0x98 + 240 + 16)),
            ("raw section inside headers", d => BitConverter.GetBytes(10).CopyTo(d, 0x98 + 240 + 20))
        };
        foreach (var corruption in corruptions)
        {
            byte[] data = Fixture(); corruption.corrupt(data);
            check(!Inspect(19045, data).CanApply(true), "abnormal PE guard " + corruption.name);
        }
        var random = new Random(0x133);
        for (int i = 0; i < 250; ++i)
        {
            byte[] data = new byte[512 + random.Next(2048)]; random.NextBytes(data);
            check(!Inspect(19045, data).CanApply(true), "arbitrary malformed PE is safely blocked " + i);
        }
        check(Win10BleStackProfile.IsAudited(19045, Win10BleStackProfile.AuditedStackSha256) &&
            !Win10BleStackProfile.IsAudited(19045, "F46BF2E7B20D4A6D2158738C587E8482828D62C2E0E8210874EEB79493DC853D"),
            "previous audited hash remains evidence label; supplied external hash is experimental, not audited");
        check(!Win10BleStackProfile.ParseExperimentalConsent(new[] { "--ble-turbo-stack-apply" }) &&
            Win10BleStackProfile.ParseExperimentalConsent(new[] { "--ble-turbo-stack-apply", Win10BleStackProfile.ExperimentalArgument }) &&
            Win10BleStackProfile.ParseExperimentalConsent(new[] { "--BLE-TURBO-STACK-APPLY", Win10BleStackProfile.ExperimentalArgument.ToUpperInvariant() }),
            "explicit CLI experiment is opt-in and case-insensitive");
        Type configurator = typeof(Win10BleStackProfile).Assembly.GetType("Y700Switch2V60Viiper.BleTurboConfigurator")!;
        foreach (string[] args in new[]
        {
            new[] { "--ble-turbo-stack-apply", Win10BleStackProfile.ExperimentalArgument, Win10BleStackProfile.ExperimentalArgument },
            new[] { "--ble-turbo-stack-apply", Win10BleStackProfile.ExperimentalArgument + "=true" },
            new[] { "--ble-turbo-stack-apply", Win10BleStackProfile.ExperimentalArgument + "s" },
            new[] { "--ble-turbo-stack-restore", Win10BleStackProfile.ExperimentalArgument },
            new[] { "--ble-turbo-apply", Win10BleStackProfile.ExperimentalArgument }
        })
        {
            bool rejected = false;
            try { Win10BleStackProfile.ParseExperimentalConsent(args); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "malformed/duplicate/misplaced experimental flag rejected before elevation or system writes");
            object result = configurator.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)!.Invoke(null,
                new object[] { args })!;
            check(!(bool)result.GetType().GetProperty("Success")!.GetValue(result)! &&
                ((string)result.GetType().GetProperty("Message")!.GetValue(result)!).Contains("实验确认"),
                "actual configurator early guard rejects invalid experiment command without administrator detection");
        }
    }
}
