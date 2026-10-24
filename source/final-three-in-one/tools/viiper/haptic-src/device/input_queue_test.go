package device

import (
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

type auditInput struct {
	Buttons uint32
	Axis    int
}

func TestInputQueuePreservesEdgesCoalescesOnlyAnalog(t *testing.T) {
	q := NewInputQueue[auditInput](64, func(a, b auditInput) bool { return a.Buttons == b.Buttons })
	q.Seed(auditInput{})
	q.Push(auditInput{Buttons: 1, Axis: 1})
	q.Push(auditInput{Buttons: 1, Axis: 2})
	q.Push(auditInput{Buttons: 0, Axis: 3})
	a, ok := q.Pop()
	if !ok || a.Buttons != 1 || a.Axis != 2 {
		t.Fatal(a, ok)
	}
	b, ok := q.Pop()
	if !ok || b.Buttons != 0 {
		t.Fatal(b, ok)
	}
	if _, ok = q.Pop(); ok {
		t.Fatal("fabricated pending state")
	}
	s := q.Stats()
	if s.Received != 3 || s.Delivered != 2 || s.Coalesced != 1 || s.Overflow != 0 {
		t.Fatal(s)
	}
}
func TestInputQueueIsBoundedAndLatestReleaseSurvives(t *testing.T) {
	q := NewInputQueue[auditInput](64, func(a, b auditInput) bool { return a.Buttons == b.Buttons })
	for i := 1; i <= 10000; i++ {
		q.Push(auditInput{Buttons: uint32(i & 1), Axis: i})
	}
	s := q.Stats()
	if s.Depth != 64 || s.MaxDepth != 64 || s.Overflow != 9936 {
		t.Fatal(s)
	}
	var last auditInput
	for {
		v, ok := q.Pop()
		if !ok {
			break
		}
		last = v
	}
	if last.Buttons != 0 || last.Axis != 10000 {
		t.Fatal(last)
	}
}
func TestInputQueueInitialSeedAndSafetyDoNotCountAsSources(t *testing.T) {
	q := NewInputQueue[auditInput](64, func(a, b auditInput) bool { return a.Buttons == b.Buttons })
	q.Seed(auditInput{})
	q.Pop()
	q.RecordRepeat()
	s := q.Stats()
	if s.Received != 0 || s.Delivered != 0 || s.Repeated != 1 {
		t.Fatal(s)
	}
	q.Push(auditInput{Buttons: 1})
	q.DiscardStale()
	s = q.Stats()
	if s.Depth != 0 || s.StaleDropped != 1 || s.Delivered != 0 {
		t.Fatal(s)
	}
}

func TestInputQueueDropsStaleHistoricalPressButKeepsLatestRelease(t *testing.T) {
	clock := time.Unix(1000, 0)
	q := newInputQueueWithClock[auditInput](64, func(a, b auditInput) bool { return a.Buttons == b.Buttons }, func() time.Time { return clock })
	q.Push(auditInput{Buttons: 1, Axis: 1})
	clock = clock.Add(20 * time.Millisecond)
	q.Push(auditInput{Buttons: 0, Axis: 2})
	clock = clock.Add(480 * time.Millisecond)
	q.Push(auditInput{Buttons: 0, Axis: 3})
	v, ok := q.Pop()
	if !ok || v.Buttons != 0 || v.Axis != 3 {
		t.Fatal("late press replayed", v, ok)
	}
	if _, ok = q.Pop(); ok {
		t.Fatal("stale history survived")
	}
	s := q.Stats()
	if s.StaleDropped != 1 || s.Delivered != 1 || s.Coalesced != 1 {
		t.Fatal(s)
	}
}

func TestInputQueueHistoricalAgeBoundary(t *testing.T) {
	for _, age := range []time.Duration{99 * time.Millisecond, 100 * time.Millisecond, 101 * time.Millisecond} {
		clock := time.Unix(1000, 0)
		q := newInputQueueWithClock[auditInput](64, func(a, b auditInput) bool { return a.Buttons == b.Buttons }, func() time.Time { return clock })
		q.Push(auditInput{Buttons: 1})
		clock = clock.Add(time.Millisecond)
		q.Push(auditInput{Buttons: 0})
		clock = time.Unix(1000, 0).Add(age)
		v, ok := q.Pop()
		if !ok {
			t.Fatal("latest state lost")
		}
		if age < MaxHistoricalInputAge {
			if v.Buttons != 1 {
				t.Fatal("prompt press lost", age)
			}
			v, ok = q.Pop()
			if !ok || v.Buttons != 0 {
				t.Fatal("prompt release lost", age)
			}
		} else if v.Buttons != 0 || q.Stats().StaleDropped != 1 {
			t.Fatal("expired press replayed", age, v, q.Stats())
		}
	}
}

func TestInputQueueFreshHeldUpdatesDoNotCreateRelease(t *testing.T) {
	clock := time.Unix(1000, 0)
	q := newInputQueueWithClock[auditInput](64, func(a, b auditInput) bool { return a.Buttons == b.Buttons }, func() time.Time { return clock })
	q.Push(auditInput{Buttons: 1, Axis: 1})
	for i := 2; i <= 10; i++ {
		clock = clock.Add(90 * time.Millisecond)
		q.Push(auditInput{Buttons: 1, Axis: i})
	}
	// The newest hold must not be thrown away even without a newer state.
	clock = clock.Add(2 * time.Second)
	v, ok := q.Pop()
	if !ok || v.Buttons != 1 || v.Axis != 10 {
		t.Fatal("current hold incorrectly released", v, ok)
	}
	if q.Stats().StaleDropped != 0 {
		t.Fatal(q.Stats())
	}
}

func TestInputQueueConcurrentBoundedFreshnessHasNoUnaccountedFrames(t *testing.T) {
	var nanos atomic.Int64
	q := newInputQueueWithClock[auditInput](64, func(a, b auditInput) bool { return a.Buttons == b.Buttons }, func() time.Time { return time.Unix(1000, nanos.Add(int64(time.Microsecond))) })
	var workers sync.WaitGroup
	for writer := 0; writer < 4; writer++ {
		workers.Add(1)
		go func(id int) {
			defer workers.Done()
			for i := 0; i < 5000; i++ {
				q.Push(auditInput{Buttons: uint32(i & 1), Axis: id*5000 + i})
				if i%4 == 0 {
					q.Stats()
				}
			}
		}(writer)
	}
	workers.Add(1)
	go func() {
		defer workers.Done()
		for i := 0; i < 10000; i++ {
			q.Pop()
		}
	}()
	workers.Wait()
	q.Push(auditInput{Buttons: 0, Axis: 999999})
	var last auditInput
	for {
		v, ok := q.Pop()
		if !ok {
			break
		}
		last = v
	}
	s := q.Stats()
	if last.Buttons != 0 || last.Axis != 999999 || s.MaxDepth > 64 || s.Depth != 0 {
		t.Fatal(last, s)
	}
	if s.Received != s.Delivered+s.Coalesced+s.Overflow+s.StaleDropped {
		t.Fatal("unaccounted queue frames", s)
	}
}
