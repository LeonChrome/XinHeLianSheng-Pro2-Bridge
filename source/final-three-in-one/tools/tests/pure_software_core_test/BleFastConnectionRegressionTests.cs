using System;
using Y700Switch2V60Viiper;

internal static class BleFastConnectionRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        BleAdaptiveMeasurement Sample(int count = 200, int? parsed = null, double duration = 1.5,
            double age = 3, double p95 = 8, double gap = 16) =>
            new(new(duration, count, parsed ?? count, age, true, false, p95, null), 0, gap);
        bool High(BleAdaptiveMeasurement value) => BleFastConnectionPolicy.IsHigh(Sample(), value);
        bool Compatible(BleAdaptiveMeasurement value) => BleFastConnectionPolicy.IsCompatible(Sample(100, p95: 16), value);

        check(High(Sample()), "two independent actual 133Hz short windows allow fast startup without an API request");
        check(High(Sample(192)), "128Hz actual FD2 and parsed boundary is accepted without inventing a ten second capture");
        check(!High(Sample(191)), "a short-window rate below 128Hz cannot become high-rate admission");
        check(!High(Sample(100, p95: 16)), "one high burst followed by 66Hz cannot pass high-rate admission");
        check(Compatible(Sample(100, p95: 16)), "native Win10 or Win11 actual 66Hz can enter compatibility mode");
        check(Compatible(Sample(90, p95: 30, gap: 60)), "exact 60Hz compatibility boundary is admitted");
        check(!Compatible(Sample(89, p95: 20)), "compatibility never promises 60Hz from fewer real samples");
        check(!BleFastConnectionPolicy.IsHigh(Sample(100, p95: 16), Sample()), "both windows must meet the high-rate requirement");
        check(!BleFastConnectionPolicy.IsCompatible(Sample(30, p95: 50), Sample(100, p95: 16)), "a low first window cannot be hidden by later recovery");

        var repeated = Sample();
        check(!BleFastConnectionPolicy.IsHigh(repeated, repeated), "one measurement cannot be reused as two independent windows");
        check(!BleFastConnectionPolicy.IsHigh(repeated, repeated with { CompletedAgeSeconds = .1 }), "the same underlying count capture cannot be reused with another age");
        check(High(Sample(200, 198)), "99 percent parsed ratio boundary is accepted when parsed Hz also meets target");
        check(!High(Sample(200, 197)), "parse losses above one percent do not pass a fast startup gate");
        check(!High(Sample(200, 201)), "more parsed than received counts are invalid");
        check(!High(Sample(-1)), "negative counters cannot manufacture input");
        check(!High(Sample(0)), "zero input never passes");

        foreach (double duration in new[] { 0, -1, 1.499, 4.001, double.NaN, double.PositiveInfinity })
            check(!High(Sample(duration: duration)), "short incomplete or unbounded duration fails fast admission " + duration);
        check(High(Sample(512, duration: 4)), "actual four second 128Hz capture is within the bounded fast policy");
        foreach (double age in new[] { -1, 40, double.NaN, double.PositiveInfinity })
            check(!High(Sample(age: age)), "stale or invalid tail fails high-rate admission " + age);
        check(!Compatible(Sample(100, age: 60, p95: 16)), "compatibility also requires a fresh input tail");
        check(!High(Sample() with { Sample = Sample().Sample with { Connected = false } }), "disconnected window is not admitted");
        foreach (double age in new[] { -1, 4.001, double.NaN, double.PositiveInfinity })
            check(!High(Sample() with { CompletedAgeSeconds = age }), "old or invalid window age cannot pass " + age);
        foreach (double gap in new[] { -1, 40.001, double.NaN, double.PositiveInfinity })
            check(!High(Sample(gap: gap)), "a burst rate cannot conceal excessive or invalid maximum silence " + gap);
        check(!High(Sample(p95: 12.01)), "high admission bounds percentile jitter");
        check(!Compatible(Sample(100, p95: 30.01)), "compatibility admission bounds percentile jitter");
        check(!Compatible(Sample(100, p95: 16, gap: 60.01)), "compatibility admission bounds maximum silence");
        check(!High(Sample() with { MaximumGapMs = null }), "unknown maximum silence cannot prove a short window is healthy");
        check(!High(Sample() with { Sample = Sample().Sample with { P95GapMs = null } }), "unknown percentile cannot prove fast-window quality");
        check(!High(Sample(p95: 8, gap: 7)), "inconsistent percentile above maximum gap is invalid");

        BleFastConnectionWindow Window(double start, double end, long sequence = 7) => new(Sample(), start, end, sequence);
        check(BleFastConnectionPolicy.IsHigh(Window(10, 11.5), Window(11.5, 13)), "monotonic adjacent captures explicitly prove non-overlap");
        check(!BleFastConnectionPolicy.IsHigh(Window(10, 11.5), Window(10, 11.5)), "duplicate capture bounds are rejected even with distinct records");
        check(!BleFastConnectionPolicy.IsHigh(Window(10, 11.5), Window(11.4, 12.9)), "partly overlapping captures cannot count packets twice");
        check(!BleFastConnectionPolicy.IsHigh(Window(10, 11.5), Window(12, 13.5)), "separated captures are not continuous fast admission evidence");
        check(!BleFastConnectionPolicy.IsHigh(Window(10, 11.5), Window(11.5, 13, 8)), "a new physical connection invalidates the earlier gate window");
        check(!BleFastConnectionPolicy.IsHigh(Window(10, 11.5), Window(11.5, 15)), "capture bounds must agree with the measured duration");
        check(!BleFastConnectionPolicy.IsHigh(Window(double.NaN, 11.5), Window(11.5, 13)), "invalid monotonic capture bounds cannot pass");

        BleFastConnectionWindow WarmWindow(double start = 10, int count = 187, double duration = 1.5,
            ushort? interval = 6, double p95 = 15, double gap = 30, double age = 3, int? parsed = null, long sequence = 7)
        {
            var measured = Sample(count, parsed, duration, age, p95, gap);
            measured = measured with { Sample = measured.Sample with { ConnectionIntervalUnits = interval } };
            return new(measured, start, start + duration, sequence);
        }
        var warmFirst = WarmWindow(duration: 187 / 123.49);
        var warmSecond = WarmWindow(warmFirst.EndedSeconds, count: 190, duration: 190 / 125.85, p95: 14.92);
        bool Warm(BleFastConnectionWindow second, bool accepted = true) =>
            BleFastConnectionPolicy.IsWarmHighCandidate(warmFirst, second, accepted);
        check(Warm(warmSecond), "observed 123.49 and 125.85Hz at actual 7.5ms can retain the accepted request during startup");
        check(!BleFastConnectionPolicy.IsHigh(warmFirst, warmSecond) && BleFastConnectionPolicy.IsCompatible(warmFirst, warmSecond),
            "near-target retention remains compatible input and never inherits a high-rate claim");
        check(!Warm(warmSecond, accepted: false), "an unaccepted or no-longer-owned fixed 7.5ms request cannot use warm retention");
        var exactWarmFirst = WarmWindow(count: 180, p95: 16, gap: 60);
        var exactWarmSecond = WarmWindow(exactWarmFirst.EndedSeconds, count: 180, p95: 16, gap: 60);
        check(BleFastConnectionPolicy.IsWarmHighCandidate(exactWarmFirst, exactWarmSecond, true),
            "both actual 120Hz windows pass the near-target boundary with bounded 16ms percentile and 60ms maximum gap");
        check(!Warm(WarmWindow(warmFirst.EndedSeconds, count: 179)), "below 120Hz cannot bypass bounded preset evaluation as near target");
        check(!Warm(WarmWindow(warmFirst.EndedSeconds, count: 100)), "66Hz compatibility is not near-target fixed-7.5ms retention");
        foreach (ushort? interval in new ushort?[] { null, 12, 24, 36, 48 })
        {
            check(!Warm(WarmWindow(warmFirst.EndedSeconds, interval: interval)), "second window needs actual six-unit interval " + interval);
            check(!BleFastConnectionPolicy.IsWarmHighCandidate(warmFirst with
            {
                Measurement = warmFirst.Measurement with { Sample = warmFirst.Measurement.Sample with { ConnectionIntervalUnits = interval } }
            }, warmSecond, true), "first window also needs actual six-unit interval " + interval);
        }
        check(!Warm(warmSecond with { ConnectionSequence = 8 }), "warm retention cannot span different physical connections");
        check(!Warm(warmSecond with { StartedSeconds = warmFirst.StartedSeconds, EndedSeconds = warmFirst.EndedSeconds }),
            "warm retention rejects duplicate capture boundaries");
        check(!Warm(warmSecond with { StartedSeconds = warmFirst.EndedSeconds - .1, EndedSeconds = warmSecond.EndedSeconds - .1 }),
            "warm retention rejects overlapping count windows");
        check(!Warm(warmSecond with { Measurement = warmSecond.Measurement with { Sample = warmSecond.Measurement.Sample with { Connected = false } } }),
            "disconnected near-target input never bypasses validation");
        check(!Warm(WarmWindow(warmFirst.EndedSeconds, age: 60)), "warm retention requires input younger than 60ms");
        check(!Warm(WarmWindow(warmFirst.EndedSeconds, p95: 16.01)), "warm retention bounds percentile jitter at 16ms");
        check(!Warm(WarmWindow(warmFirst.EndedSeconds, gap: 60.01)), "warm retention does not conceal excessive maximum silence");
        check(!Warm(WarmWindow(warmFirst.EndedSeconds, parsed: 185)), "warm retention still requires at least 99 percent parsed integrity");
        check(!Warm(warmSecond with { Measurement = warmSecond.Measurement with { CompletedAgeSeconds = 5 } }),
            "stale near-target windows cannot preserve an active request");

        bool Preserve(bool admitted = true, bool owned = true, bool connected = true, bool running = true,
            bool recent = true, ushort interval = 6, double fd2 = 123.49, double parsed = 123.49, double age = 2) =>
            BleFastConnectionPolicy.ShouldPreserveMinimumInterval(admitted, owned, connected, running, recent, interval, fd2, parsed, age);
        check(Preserve() && Preserve(fd2: 125.85, parsed: 125.85), "live warm-admitted 123Hz or 125Hz retains owned minimum interval without automatic preset cycling");
        check(Preserve(fd2: 120, parsed: 120, age: 499), "runtime retention accepts real 120Hz boundary with an input tail younger than 500ms");
        check(Preserve(fd2: 125, parsed: 123.75), "runtime retention accepts exact 99 percent parsed integrity when both rates are at least 120Hz");
        check(!Preserve(admitted: false) && !Preserve(owned: false), "runtime retention requires warm admission and ownership of the fixed 7.5ms request");
        check(!Preserve(connected: false) && !Preserve(running: false) && !Preserve(recent: false), "offline stopped or old-rate evidence cannot suppress automatic optimization");
        check(!Preserve(interval: 12) && !Preserve(interval: 0), "a reported 15ms or unknown interval is never retained as minimum interval");
        check(!Preserve(fd2: 66, parsed: 66) && !Preserve(fd2: 119.99, parsed: 119.99), "runtime retention cannot conceal 66Hz or below-120Hz degradation");
        check(!Preserve(fd2: 125, parsed: 123.74) && !Preserve(fd2: 125, parsed: 125.01), "runtime retention rejects parse loss or parsed rates above received rates");
        check(!Preserve(fd2: 120, parsed: 119.99), "runtime parsed input must independently reach 120Hz");
        foreach (double age in new[] { -1, 500, double.NaN, double.PositiveInfinity })
            check(!Preserve(age: age), "runtime retention rejects stale or invalid input age " + age);
        foreach (double hz in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity })
            check(!Preserve(fd2: hz) && !Preserve(parsed: hz), "runtime retention rejects nonfinite measured rates " + hz);
    }
}
