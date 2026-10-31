package handler_test

import (
	"encoding/json"
	"testing"
)

// The API's stable device identity is unchanged. v2 also advertises the
// producer/held-repeat accounting introduced by source-paced input; keep
// exact response comparisons rather than ignoring extra metadata.
func initialInputTelemetryResponse(t *testing.T, response string) string {
	t.Helper()
	var value any
	if err := json.Unmarshal([]byte(response), &value); err != nil {
		t.Fatal(err)
	}
	var expand func(any)
	expand = func(value any) {
		switch node := value.(type) {
		case map[string]any:
			if node["type"] == "xbox360" {
				if specific, ok := node["deviceSpecific"].(map[string]any); ok {
					specific["source_paced"] = false
					specific["input_transfer"] = map[string]any{
						"source_received": 0, "source_delivered": 0,
						"analog_coalesced": 0, "edge_overflow": 0,
						"stale_dropped": 0, "compatibility_repeated": 0,
						"queue_depth": 1, "queue_max_depth": 1,
					}
				}
			}
			for _, child := range node {
				expand(child)
			}
		case []any:
			for _, child := range node {
				expand(child)
			}
		}
	}
	expand(value)
	wire, err := json.Marshal(value)
	if err != nil {
		t.Fatal(err)
	}
	return string(wire)
}
