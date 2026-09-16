using Y700Switch2V55Manager;

static void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

BleInputStatus dualSense = BleInputStatusParser.Parse(
    "{\"ble\":\"connected\",\"input_live\":true,\"input_updates\":42,\"input_age_ms\":8,\"input_rate_millihz\":250000}");
Expect(dualSense.Ready, "DualSense input schema should be ready.");
Expect(dualSense.Schema == "input", "DualSense schema was not selected.");

BleInputStatus pro2 = BleInputStatusParser.Parse(
    "{\"ble\":\"connected\",\"live\":\"active\",\"live_updates\":99,\"live_age_ms\":4,\"ble_input_actual_mhz\":200000}");
Expect(pro2.Ready, "Pro2 live schema should be ready.");
Expect(pro2.Schema == "live", "Pro2 live schema was not selected.");
Expect(pro2.RateMilliHz == 200000, "Pro2 input rate was not parsed.");

BleInputStatus stalePro2 = BleInputStatusParser.Parse(
    "{\"ble\":\"connected\",\"live\":\"active\",\"live_updates\":99,\"live_age_ms\":501}");
Expect(!stalePro2.Ready, "Stale Pro2 input must not be treated as ready.");

BleInputStatus noPro2Input = BleInputStatusParser.Parse(
    "{\"ble\":\"connected\",\"live\":\"none\",\"live_updates\":0,\"live_age_ms\":-1}");
Expect(!noPro2Input.Ready, "Connected Pro2 without notifications must not be ready.");

BleInputStatus legacy = BleInputStatusParser.Parse("{\"ble\":\"connected\",\"version\":\"5.2\"}");
Expect(legacy.Ready, "Legacy connected firmware should use transport fallback.");
Expect(!legacy.HasMetrics, "Legacy firmware should not claim input metrics.");

BleInputStatus disconnected = BleInputStatusParser.Parse(
    "{\"ble\":\"idle\",\"live\":\"active\",\"live_updates\":12,\"live_age_ms\":2}");
Expect(!disconnected.Ready, "Disconnected transport must not be ready.");

BleInputStatus priority = BleInputStatusParser.Parse(
    "{\"ble\":\"connected\",\"live\":\"active\",\"live_updates\":12,\"live_age_ms\":2,\"input_live\":false,\"input_updates\":0,\"input_age_ms\":-1}");
Expect(priority.Schema == "input" && !priority.Ready,
    "DualSense input schema must take priority when both schemas are present.");

Console.WriteLine("ble_input_status_test: passed");
