package device

import (
	"testing"
	"time"
)

func TestActiveReplacementOutlivesRetiredStream(t *testing.T) {
	s := NewStreamLifetime()
	defer s.Close()
	a, _ := s.Acquire()
	b, _ := s.Acquire()
	removed := make(chan struct{}, 1)
	s.Release(a, 10*time.Millisecond, func() { removed <- struct{}{} })
	select {
	case <-removed:
		t.Fatal("old stream deleted live replacement")
	case <-time.After(30 * time.Millisecond):
	}
	s.Release(b, 10*time.Millisecond, func() { removed <- struct{}{} })
	select {
	case <-removed:
	case <-time.After(time.Second):
		t.Fatal("idle cleanup never fired")
	}
	if _, ok := s.Acquire(); ok {
		t.Fatal("lease acquired after deletion committed")
	}
}

func TestFourLifetimesIsolateIdleRemoval(t *testing.T) {
	all := make([]*StreamLifetime, 4)
	ids := make([]uint64, 4)
	done := make(chan int, 4)
	for i := range all {
		all[i] = NewStreamLifetime()
		defer all[i].Close()
		ids[i], _ = all[i].Acquire()
	}
	all[1].Release(ids[1], 10*time.Millisecond, func() { done <- 1 })
	select {
	case i := <-done:
		if i != 1 {
			t.Fatal(i)
		}
	case <-time.After(time.Second):
		t.Fatal("idle slot not removed")
	}
	for _, i := range []int{0, 2, 3} {
		if _, ok := all[i].Acquire(); !ok {
			t.Fatalf("sibling %d removed", i)
		}
	}
	select {
	case i := <-done:
		t.Fatalf("unexpected cleanup %d", i)
	default:
	}
}
