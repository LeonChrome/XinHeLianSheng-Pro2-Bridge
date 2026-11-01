using System;
using Y700Switch2V60Viiper;

// v2.5 auto sleep / button wake. Vectors come from the real Pro2 (fw 1.1.5) pairing captured
// 2026-11-01 (artifacts/pro2-pair-wake-probe-20261101/run-20261101-094212-pair-wake) and the
// public ndeadly sample. No Bluetooth I/O.
internal static class SleepWakeRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        static byte[] H(string hex) => Convert.FromHexString(hex);
        static string X(byte[] b) => Convert.ToHexString(b);
        const ulong pc = 0xBCC746A58DD2;

        check(X(Pro2SleepWakeProtocol.SleepCommand()) == "06910102000C0000000000000000000000000000",
            "sleep command is exactly 06/02 with 12 zero bytes (hardware verified)");
        check(X(Pro2SleepWakeProtocol.ReversedAddress(pc)) == "D28DA546C7BC",
            "host address is sent byte-reversed");
        check(X(Pro2SleepWakeProtocol.ExchangeAddressesCommand(pc)) == "15910101000E00000002D28DA546C7BCD18DA546C7BC",
            "15/01 matches the request the controller accepted on hardware");
        check(X(Pro2SleepWakeProtocol.FinaliseCommand()) == "159101030001000000", "15/03 finalise bytes");
        check(X(Pro2SleepWakeProtocol.ExchangeKeysCommand(H("5B226727C69B07D07F3EEA9DBE6BF5F1"))) ==
              "1591010400110000005B226727C69B07D07F3EEA9DBE6BF5F1", "15/04 key exchange layout");

        // ndeadly public sample
        byte[] docLtk = Pro2SleepWakeProtocol.ComputeLtk(H("3503e92982877124bea80c664615834b"), H("5cf6ee792cdf05e1ba2b6325c41a5f10"));
        check(X(Pro2SleepWakeProtocol.ExpectedConfirmResponse(docLtk, H("6fc6df8ad8fedf15bb8c15e91f320544"))) ==
              "134C97F511B9B6DD4D86FD40F536E9ED", "AES confirm matches public sample");

        // real controller reply
        byte[] ltk = Pro2SleepWakeProtocol.ComputeLtk(H("5B226727C69B07D07F3EEA9DBE6BF5F1"), H("5CF6EE792CDF05E1BA2B6325C41A5F10"));
        check(X(ltk) == "07D4895EEA440231C51589B87A71AAE1", "LTK = A1 xor B1 (hardware)");
        check(X(Pro2SleepWakeProtocol.ExpectedConfirmResponse(ltk, H("06616FDC241F8DC7FA84B89833C0D2EB"))) ==
              "93AAD87FB898F48F64A5165D83876033", "AES confirm matches the real controller reply");

        byte[] afterPair = H("0200544200008C42BCC746A58DD200000000");
        check(Pro2SleepWakeProtocol.IsHostStored(afterPair, pc), "flash read-back recognises this PC as paired");
        check(!Pro2SleepWakeProtocol.IsHostStored(afterPair, 0xBCC746A58DD3), "another host is not treated as paired");
        check(!Pro2SleepWakeProtocol.IsHostStored(H("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"), pc), "erased flash (count 0xFF) is unpaired");
        check(!Pro2SleepWakeProtocol.IsHostStored(H("0000000000000000BCC746A58DD200000000"), pc), "count 0 is unpaired even with stale bytes");
        check(!Pro2SleepWakeProtocol.IsHostStored(H("0200"), pc), "short read is unpaired");

        // idle tracker
        var tracker = new Pro2UserActivityTracker();
        GamepadState Neutral() => GamepadState.Neutral();
        check(tracker.Note(Neutral(), 1), "first sample initialises the tracker");
        check(!tracker.Note(Neutral(), 2), "neutral controller is idle");
        var noisy = Neutral(); noisy.Lx = (ushort)(GamepadState.AxisCenter + 150); noisy.Ry = (ushort)(GamepadState.AxisCenter - 150);
        check(!tracker.Note(noisy, 3), "small stick noise is idle");
        var imu = Neutral(); imu.GyroX = 3000; imu.AccelZ = 4000; imu.GyroValid = true; imu.AccelValid = true;
        check(!tracker.Note(imu, 4), "IMU movement alone (controller on desk / picked up) is idle");
        var press = Neutral(); press.Buttons = GamepadButtons.South;
        check(tracker.Note(press, 5) && tracker.LastActivityTicks == 5, "button press is activity");
        check(tracker.Note(press, 6), "holding a button keeps counting as activity");
        check(tracker.Note(Neutral(), 7), "button release is activity");
        check(!tracker.Note(Neutral(), 8), "back to idle after release");
        var held = Neutral(); held.Ly = 4000;
        check(tracker.Note(held, 9) && tracker.Note(held, 10), "stick held forward counts as activity every report");
        tracker.Note(Neutral(), 11);
        var trigger = Neutral(); trigger.R2 = GamepadState.TriggerMax;
        check(tracker.Note(trigger, 12), "trigger press is activity");
        tracker.Reset(100);
        check(tracker.LastActivityTicks == 100, "reset on new live session restarts the idle clock");
    }
}
