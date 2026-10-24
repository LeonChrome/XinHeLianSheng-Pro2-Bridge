namespace Y700Switch2V60Viiper;

// Owned by one ViiperBridgeSession/input consumer, never shared between slots.
// Invalid angular velocity must stop immediately; invalid gravity must not
// rotate a tilted controller back to an arbitrary synthetic resting pose.
public sealed class MotionValidityPolicy
{
    private bool hasAcceleration;
    private short accelX, accelY, accelZ;

    public bool HasAcceleration => hasAcceleration;

    public GamepadState Resolve(GamepadState? input)
    {
        input ??= GamepadState.Neutral();
        if (input.AccelValid)
        {
            accelX = input.AccelX;
            accelY = input.AccelY;
            accelZ = input.AccelZ;
            hasAcceleration = true;
        }
        if (input.AccelValid && input.GyroValid)
            return input;

        // Only the output copy gets held gravity. Source validity, controls,
        // frame counters and clocks continue to describe the actual BLE frame.
        GamepadState resolved = input.Clone();
        if (!input.AccelValid)
        {
            resolved.AccelX = hasAcceleration ? accelX : (short)0;
            resolved.AccelY = hasAcceleration ? accelY : (short)0;
            resolved.AccelZ = hasAcceleration ? accelZ : (short)4096;
            resolved.AccelValid = true; // known held/default output gravity
        }
        if (!input.GyroValid)
        {
            resolved.GyroX = resolved.GyroY = resolved.GyroZ = 0;
            resolved.GyroValid = false;
        }
        return resolved;
    }

    public void Reset()
    {
        hasAcceleration = false;
        accelX = accelY = accelZ = 0;
    }
}
