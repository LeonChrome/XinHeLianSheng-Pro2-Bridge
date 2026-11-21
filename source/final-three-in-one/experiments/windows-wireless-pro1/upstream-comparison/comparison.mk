# Include after the pinned port/windows-winusb/Makefile. All protocol objects
# and their build options come from that makefile, including btkeyLib.o.
STOCK_OBJECTS = $(CORE_OBJ) $(COMMON_OBJ) $(CLASSIC_OBJ) $(SDP_CLIENT) btstack_ring_buffer.o hid_device.o btstack_hid_parser.o btkeyLib.o
CMP_OBJECTS = $(filter-out main.o hci_transport_h2_winusb.o,$(STOCK_OBJECTS))

comparison_entry.o: $(COMPARISON_DIR)/comparison_entry.c $(COMPARISON_DIR)/comparison_target.h main.c
	$(CC) $(CFLAGS) -I$(BTSTACK_ROOT)/example -c $< -o $@
comparison_transport.o: $(COMPARISON_DIR)/comparison_transport.c $(COMPARISON_DIR)/comparison_target.h $(BTSTACK_ROOT)/platform/windows/hci_transport_h2_winusb.c
	$(CC) $(CFLAGS) -c $< -o $@
Pro1UpstreamComparison.exe: comparison_entry.o comparison_transport.o $(CMP_OBJECTS)
	$(CC) $^ $(LDFLAGS) -municode -o $@

comparison_entry_realtek.o: $(COMPARISON_DIR)/comparison_entry.c $(COMPARISON_DIR)/comparison_target.h $(COMPARISON_DIR)/relay_stdio.h main.c
	$(CC) $(CFLAGS) -DCOMPARISON_REALTEK -I$(BTSTACK_ROOT)/example -c $< -o $@
comparison_transport_realtek.o: $(COMPARISON_DIR)/comparison_transport.c $(COMPARISON_DIR)/comparison_target.h $(BTSTACK_ROOT)/platform/windows/hci_transport_h2_winusb.c
	$(CC) $(CFLAGS) -DCOMPARISON_REALTEK -c $< -o $@
comparison_realtek_chipset.o: $(COMPARISON_DIR)/comparison_realtek_chipset.c $(COMPARISON_DIR)/comparison_target.h $(BTSTACK_ROOT)/chipset/realtek/btstack_chipset_realtek.c
	$(CC) $(CFLAGS) -DCOMPARISON_REALTEK -c $< -o $@
.PHONY: refresh_comparison_firmware
comparison_firmware.o: $(COMPARISON_DIR)/comparison_firmware.rc refresh_comparison_firmware
	x86_64-w64-mingw32-windres -I "$(FIRMWARE_DIR)" -i "$<" -o $@
comparison_relay_stdio.o: $(COMPARISON_DIR)/relay_stdio.c $(COMPARISON_DIR)/relay_stdio.h $(COMPARISON_DIR)/relay_nfc.h
	$(CC) $(CFLAGS) -I$(BTSTACK_ROOT)/example -c $< -o $@
comparison_relay_nfc.o: $(COMPARISON_DIR)/relay_nfc.c $(COMPARISON_DIR)/relay_nfc.h $(BTSTACK_ROOT)/example/btkeyLib.c
	$(CC) $(CFLAGS) -I$(BTSTACK_ROOT)/example -c $< -o $@
Pro1UpstreamComparisonRealtek.exe: comparison_entry_realtek.o comparison_transport_realtek.o comparison_realtek_chipset.o comparison_firmware.o comparison_relay_stdio.o comparison_relay_nfc.o $(filter-out btkeyLib.o btstack_chipset_realtek.o,$(CMP_OBJECTS))
	$(CC) $^ $(LDFLAGS) -lwinmm -municode -o $@
