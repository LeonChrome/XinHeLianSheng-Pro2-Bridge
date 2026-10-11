using System;
using Y700Switch2V60Viiper;

internal static class BleMeasuredTrialRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        BleMeasuredTrialSample Sample(int raw = 660, int parsed = 660) =>
            new(10, raw, parsed, 4, true, true, 15, 12);
        var before = Sample() with { RequestedSuccessfully = false };
        BleMeasuredTrialResult Evaluate(BleMeasuredTrialSample after) => BleMeasuredTrialPolicy.Evaluate(before, after);
        void Reject(BleMeasuredTrialSample after, string code, string name)
        {
            var result = Evaluate(after);
            check(!result.RetainRequest && result.Code == code, name);
        }

        var target = Evaluate(Sample(1330, 1330) with { ConnectionIntervalUnits = 24, P95GapMs = 7.8 });
        check(target.RetainRequest && target.Code == "target_reached",
            "real 133Hz accepted even with a reported 30ms interval; one notification per event not assumed");
        check(BleMeasuredTrialPolicy.Evaluate(before, Sample(1000, 990)).Code == "measured_improvement",
            "measured partial improvement is retained without being described as 128Hz acceptance");
        check(BleMeasuredTrialPolicy.Evaluate(Sample(1270, 1270), Sample(1280, 1280)).RetainRequest,
            "reaching actual 128Hz can retain a smaller final improvement");
        check(!BleMeasuredTrialPolicy.Evaluate(Sample(1280, 1280), Sample(1330, 1330)).RetainRequest,
            "already sufficient native FD2 needs no additional request");
        check(Evaluate(Sample(693, 693)).RetainRequest,
            "exact 5 percent and at least 3Hz effective improvement passes");
        check(!BleMeasuredTrialPolicy.Evaluate(Sample(400, 400), Sample(420, 420)).RetainRequest,
            "5 percent without the absolute 3Hz gain does not pass");
        check(!BleMeasuredTrialPolicy.Evaluate(Sample(1000, 1000), Sample(1030, 1030)).RetainRequest,
            "3Hz without 5 percent relative gain does not pass");
        Reject(Sample(), "no_measured_improvement", "API success with unchanged real input is rolled back");
        Reject(Sample(650, 650), "measured_rate_regressed", "a lower real notification rate is rolled back");
        Reject(Sample(670, 650), "measured_rate_regressed", "parsed input decline cannot be concealed by more FD2");
        Reject(Sample(1000, 949), "parsed_integrity_failed", "95 percent parsed integrity required");
        check(Evaluate(Sample(1000, 950)).RetainRequest, "exact 95 percent parsed integrity boundary accepted");
        Reject(Sample(0, 0), "insufficient_samples", "zero input despite API success is rolled back");
        Reject(Sample(19, 19), "insufficient_samples", "at least twenty actual packets required");
        Reject(Sample(1330, 1330) with { RequestedSuccessfully = false }, "request_not_successful",
            "an Unreachable or otherwise unsuccessful request cannot be retained");
        Reject(Sample(1330, 1330) with { Connected = false }, "link_not_live", "disconnected after trial is rolled back");
        check(!BleMeasuredTrialPolicy.Evaluate(before with { Connected = false }, Sample(1330, 1330)).RetainRequest,
            "nonlive baseline cannot establish a before and after comparison");
        foreach (double age in new[] { -1, 500, 2000, double.NaN, double.PositiveInfinity })
            Reject(Sample(1330, 1330) with { LastInputAgeMs = age }, "input_not_fresh",
                "stale or invalid latest input overrides a historical high rate " + age);
        check(Evaluate(Sample(1330, 1330) with { LastInputAgeMs = 499.9 }).RetainRequest,
            "latest input age boundary is strictly less than 500ms");
        check(!BleMeasuredTrialPolicy.Evaluate(before with { LastInputAgeMs = 500 }, Sample(1330, 1330)).RetainRequest,
            "stalled native tail is not valid baseline evidence");
        Reject(Sample(1330, 1330) with { DurationSeconds = 9.99 }, "incomplete_window", "full ten second capture required");
        Reject(Sample(2660, 2660) with { DurationSeconds = 20 }, "unequal_windows",
            "inconsistent capture lengths cannot be called matching windows");
        check(Evaluate(Sample(1397, 1397) with { DurationSeconds = 10.5 }).RetainRequest,
            "matching windows tolerate at most five percent scheduler overrun");
        Reject(Sample(1397, 1397) with { DurationSeconds = 10.51 }, "unequal_windows", "window tolerance has a bound");
        foreach (double duration in new[] { 0, -1, double.NaN, double.PositiveInfinity })
            Reject(Sample(1330, 1330) with { DurationSeconds = duration }, "invalid_measurement",
                "invalid capture duration does not manufacture a measured rate " + duration);
        Reject(Sample(-1, -1), "invalid_measurement", "negative packet counters are invalid");
        Reject(Sample(1330, 1331), "invalid_measurement", "more parsed than received packets is invalid");
        Reject(Sample(1330, 1330) with { P95GapMs = double.NaN }, "invalid_measurement", "invalid optional gap is explicit");
        check(Evaluate(Sample(1330, 1330) with { P95GapMs = null, ConnectionIntervalUnits = null }).RetainRequest,
            "unavailable interval or percentile cannot replace real FD2 evidence with a guessed limit");
        check(Sample(660, 650).Fd2Hz == 66 && Sample(660, 650).ParsedHz == 65,
            "frequency divides actual counts by the complete window including a silent tail");

        bool CanTry(bool win11 = true, bool request = true, bool read = true, bool connected = true,
            bool busy = false, bool measuring = false, bool tried = false, double hz = 66) =>
            BleMeasuredTrialPolicy.CanTryInApp(win11, request, read, connected, busy, measuring, tried, hz);
        check(CanTry(), "an idle live low rate Win11 connection can expose the explicit trial button");
        check(!CanTry(win11: false) && !CanTry(request: false) && !CanTry(read: false) && !CanTry(connected: false),
            "Win10 missing APIs and disconnected state never permit in app trial");
        check(!CanTry(busy: true) && !CanTry(measuring: true) && !CanTry(tried: true),
            "busy measurement or an already attempted connection blocks repeated trials");
        foreach (double hz in new[] { 0, -1, 128, 133.3, double.NaN, double.PositiveInfinity })
            check(!CanTry(hz: hz), "healthy or unmeasured native rate does not request a parameter trial " + hz);
        check(CanTry(hz: 127.9), "explicit trial offered only below the native 128Hz boundary");
    }
}
