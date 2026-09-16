package usb

import (
	"context"
	"testing"
	"time"

	"github.com/stretchr/testify/require"
)

func TestEndpointTransferGatesSerializeOneEndpoint(t *testing.T) {
	gates := newEndpointTransferGates()

	releaseFirst, acquired := gates.acquire(context.Background(), 1, 0)
	require.True(t, acquired)

	blockedCtx, cancelBlocked := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancelBlocked()
	_, acquired = gates.acquire(blockedCtx, 1, 0)
	require.False(t, acquired, "a second URB on the same endpoint must wait")

	releaseOther, acquired := gates.acquire(context.Background(), 2, 0)
	require.True(t, acquired, "different endpoints must remain independent")
	releaseOther()

	releaseFirst()
	releaseNext, acquired := gates.acquire(context.Background(), 1, 0)
	require.True(t, acquired)
	releaseNext()
}

func TestEndpointTransferGatesEnforceMinimumCompletionInterval(t *testing.T) {
	gates := newEndpointTransferGates()
	const interval = 20 * time.Millisecond

	releaseFirst, acquired := gates.acquire(context.Background(), 1, interval)
	require.True(t, acquired)
	releaseFirst()

	started := time.Now()
	releaseSecond, acquired := gates.acquire(context.Background(), 1, interval)
	require.True(t, acquired)
	elapsed := time.Since(started)
	releaseSecond()

	require.GreaterOrEqual(t, elapsed, 15*time.Millisecond)
}

func TestEndpointTransferGatesUseFixedCadence(t *testing.T) {
	gates := newEndpointTransferGates()
	const interval = 10 * time.Millisecond

	release, acquired := gates.acquire(context.Background(), 1, interval)
	require.True(t, acquired)
	release()

	started := time.Now()
	for range 3 {
		release, acquired = gates.acquire(context.Background(), 1, interval)
		require.True(t, acquired)
		time.Sleep(time.Millisecond)
		release()
	}

	elapsed := time.Since(started)
	require.GreaterOrEqual(t, elapsed, 27*time.Millisecond)
	require.Less(t, elapsed, 45*time.Millisecond)
}
