using LiteMonitor.src.SystemServices;

// Self-contained smoke checks: no EC or PawnIO device is opened in CI.
static void Expect(string name, int actual, int expected)
{
    if (actual != expected)
        throw new Exception($"{name}: wanted {expected}, received {actual}");
    Console.WriteLine($"PASS {name}: {actual}");
}

Expect("stopped fan is valid zero", ThinkPadFanProvider.DecodeRpm(0, 0), 0);
Expect("ordinary 2600 RPM", ThinkPadFanProvider.DecodeRpm(0x28, 0x0a), 2600);
Expect("maximum valid EC RPM", ThinkPadFanProvider.DecodeRpm(0xff, 0x1f), 8191);
Expect("invalid EC sentinel", ThinkPadFanProvider.DecodeRpm(0xff, 0xff), -1);
Expect("invalid above max", ThinkPadFanProvider.DecodeRpm(0x00, 0x20), -1);
Expect("signed byte is masked", ThinkPadFanProvider.DecodeRpm(-1, 0), 255);
Console.WriteLine("ThinkPad RPM decoding smoke tests passed.");
