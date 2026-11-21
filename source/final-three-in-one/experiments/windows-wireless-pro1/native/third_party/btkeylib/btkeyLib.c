//==============================================================================================
/**
 * @file	btkeyLib.c
 * @brief	Bluetoothアダプタをワイヤレスコントローラ(Pro Controller)に偽装する
 * @date	2021.06.20
 *
 * 構成:
 *   1. 設定と HID レポートディスクリプタ
 *   2. Switch 本体へ返す定型応答パケット
 *   3. amiibo (NFC) データの組み立て
 *   4. コントローラ状態と公開 API
 *   5. 入力レポートの生成と送信
 *   6. Switch 本体からの受信パケット解析 (ステートマシンの入力)
 *   7. can-send-now ハンドラ (ステートマシンの出力)
 *   8. SDP レコード登録と起動処理
 */
 //==============================================================================================
#ifndef _WIN32
#define _DEFAULT_SOURCE  // Required for usleep() in C99 mode on UNIX-compliant systems
#endif
#define BTSTACK_FILE__ "btkeyLib.c"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <inttypes.h>
#include <time.h>
#ifdef _WIN32
#include <windows.h>
#else
#include <unistd.h>
#endif
#include "btstack.h"
#include "btkeyLib.h"

//----------------------------------------------------------
// 1. 設定
//----------------------------------------------------------

// amiibo (NFC) エミュレーションを含めるか
#ifndef BTKEYLIB_ENABLE_AMIIBO
#define BTKEYLIB_ENABLE_AMIIBO 1
#endif

// 振動データの受信通知を含めるか
#ifndef BTKEYLIB_ENABLE_RUMBLE
#define BTKEYLIB_ENABLE_RUMBLE 1
#endif

static const char hid_device_name[] = "Pro Controller";

// 入力レポート (0x30 / 0x31) の共通部分のサイズ
#define INPUT_REPORT_SIZE       50
// 6軸センサ1サンプル分のサイズ。入力レポートには3サンプル分が含まれる
#define IMU_FRAME_SIZE          12
#define IMU_FRAMES_PER_REPORT   3
#define IMU_FRAME_OFFSET        14
// NFC/IR MCU データ (入力レポート 0x31 の後半)
#define NFC_PAYLOAD_SIZE        312
// サブコマンド応答は常にこのサイズで送る
#define SUBCOMMAND_REPLY_SIZE   64

#define HID_INPUT_REPORT_PREFIX 0xA1
#define INPUT_REPORT_ID_STANDARD 0x30
#define INPUT_REPORT_ID_NFC_IR   0x31

// バッテリ残量(満充電) + 接続情報(Bluetooth/Pro Controller)
#define BATTERY_CONNECTION_INFO 0x8E

// 入力レポートの送信間隔 (60Hz)
#define INPUT_REPORT_INTERVAL_S (1.0 / 60.0)

// send_padcolor() が書き換える reply1050 内のオフセット
#define SPI_COLOR_OFFSET        0x15

// サブコマンド応答が申告する Bluetooth アドレスの位置。
// 実機キャプチャ由来の値が埋まっているため、起動時にドングルの実アドレスで上書きする
#define REPLY01_BD_ADDR_OFFSET  17
#define REPLY02_BD_ADDR_OFFSET  20

// 出力レポート (Switch → コントローラ) 内のオフセット。
// report[0] がタイマ、report[1..8] が振動データ、report[9] 以降がサブコマンド。
#define OUT_REPORT_TIMER        0
#define OUT_REPORT_SUBCMD       9
#define OUT_REPORT_ARG(n)       (OUT_REPORT_SUBCMD + 1 + (n))

// Switch → コントローラ の出力レポート ID
#define OUT_REPORT_ID_SUBCOMMAND 0x01
#define OUT_REPORT_ID_RUMBLE     0x10

//----------------------------------------------------------
// HID レポートディスクリプタ
//----------------------------------------------------------
static const uint8_t reportMap[] = {
    0x05, 0x01,        // Usage Page (Generic Desktop Ctrls)
    0x09, 0x05,        // Usage (Game Pad)
    0xA1, 0x01,        // Collection (Application)
    0x06, 0x01, 0xFF,  //   Usage Page (Vendor Defined 0xFF01)

    0x85, 0x21,  //   Report ID (33)
    0x09, 0x21,  //   Usage (0x21)
    0x75, 0x08,  //   Report Size (8)
    0x95, 0x30,  //   Report Count (48)
    0x81, 0x02,  //   Input (Data,Var,Abs,No Wrap,Linear,Preferred State,No Null
                 //   Position)

    0x85, 0x30,  //   Report ID (48)
    0x09, 0x30,  //   Usage (0x30)
    0x75, 0x08,  //   Report Size (8)
    0x95, 0x30,  //   Report Count (48)
    0x81, 0x02,  //   Input (Data,Var,Abs,No Wrap,Linear,Preferred State,No Null
                 //   Position)

    0x85, 0x31,        //   Report ID (49)
    0x09, 0x31,        //   Usage (0x31)
    0x75, 0x08,        //   Report Size (8)
    0x96, 0x69, 0x01,  //   Report Count (361)
    0x81, 0x02,  //   Input (Data,Var,Abs,No Wrap,Linear,Preferred State,No Null
                 //   Position)

    0x85, 0x32,        //   Report ID (50)
    0x09, 0x32,        //   Usage (0x32)
    0x75, 0x08,        //   Report Size (8)
    0x96, 0x69, 0x01,  //   Report Count (361)
    0x81, 0x02,  //   Input (Data,Var,Abs,No Wrap,Linear,Preferred State,No Null
                 //   Position)

    0x85, 0x33,        //   Report ID (51)
    0x09, 0x33,        //   Usage (0x33)
    0x75, 0x08,        //   Report Size (8)
    0x96, 0x69, 0x01,  //   Report Count (361)
    0x81, 0x02,  //   Input (Data,Var,Abs,No Wrap,Linear,Preferred State,No Null
                 //   Position)

    0x85, 0x3F,  //   Report ID (63)
    0x05, 0x09,  //   Usage Page (Button)
    0x19, 0x01,  //   Usage Minimum (0x01)
    0x29, 0x10,  //   Usage Maximum (0x10)
    0x15, 0x00,  //   Logical Minimum (0)
    0x25, 0x01,  //   Logical Maximum (1)
    0x75, 0x01,  //   Report Size (1)
    0x95, 0x10,  //   Report Count (16)
    0x81, 0x02,  //   Input (Data,Var,Abs,No Wrap,Linear,Preferred State,No Null
                 //   Position)
    0x05, 0x01,  //   Usage Page (Generic Desktop Ctrls)
    0x09, 0x39,  //   Usage (Hat switch)
    0x15, 0x00,  //   Logical Minimum (0)
    0x25, 0x07,  //   Logical Maximum (7)
    0x75, 0x04,  //   Report Size (4)
    0x95, 0x01,  //   Report Count (1)
    0x81, 0x42,  //   Input (Data,Var,Abs,No Wrap,Linear,Preferred State,Null
                 //   State)
    0x05, 0x09,  //   Usage Page (Button)
    0x75, 0x04,  //   Report Size (4)
    0x95, 0x01,  //   Report Count (1)
    0x81, 0x01,  //   Input (Const,Array,Abs,No Wrap,Linear,Preferred State,No
                 //   Null Position)
    0x05, 0x01,  //   Usage Page (Generic Desktop Ctrls)
    0x09, 0x30,  //   Usage (X)
    0x09, 0x31,  //   Usage (Y)
    0x09, 0x33,  //   Usage (Rx)
    0x09, 0x34,  //   Usage (Ry)
    0x16, 0x00, 0x00,              //   Logical Minimum (0)
    0x27, 0xFF, 0xFF, 0x00, 0x00,  //   Logical Maximum (65534)
    0x75, 0x10,                    //   Report Size (16)
    0x95, 0x04,                    //   Report Count (4)
    0x81, 0x02,  //   Input (Data,Var,Abs,No Wrap,Linear,Preferred State,No Null
                 //   Position)
    0x06, 0x01, 0xFF,  //   Usage Page (Vendor Defined 0xFF01)

    0x85, 0x01,  //   Report ID (1)
    0x09, 0x01,  //   Usage (0x01)
    0x75, 0x08,  //   Report Size (8)
    0x95, 0x30,  //   Report Count (48)
    0x91, 0x02,  //   Output (Data,Var,Abs,No Wrap,Linear,Preferred State,No
                 //   Null Position,Non-volatile)

    0x85, 0x10,  //   Report ID (16)
    0x09, 0x10,  //   Usage (0x10)
    0x75, 0x08,  //   Report Size (8)
    0x95, 0x09,  //   Report Count (9)
    0x91, 0x02,  //   Output (Data,Var,Abs,No Wrap,Linear,Preferred State,No
                 //   Null Position,Non-volatile)

    0x85, 0x11,  //   Report ID (17)
    0x09, 0x11,  //   Usage (0x11)
    0x75, 0x08,  //   Report Size (8)
    0x95, 0x30,  //   Report Count (48)
    0x91, 0x02,  //   Output (Data,Var,Abs,No Wrap,Linear,Preferred State,No
                 //   Null Position,Non-volatile)

    0x85, 0x12,  //   Report ID (18)
    0x09, 0x12,  //   Usage (0x12)
    0x75, 0x08,  //   Report Size (8)
    0x95, 0x30,  //   Report Count (48)
    0x91, 0x02,  //   Output (Data,Var,Abs,No Wrap,Linear,Preferred State,No
                 //   Null Position,Non-volatile)
    0xC0,        // End Collection
};

//----------------------------------------------------------
// 2. Switch 本体へ返す定型応答パケット
//----------------------------------------------------------
static const uint8_t reply00[] = { 0xa1, 0x30, 0x00, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
// offset 20..25 の Bluetooth アドレスは update_bd_addr_in_replies() で実アドレスに差し替わる
static uint8_t reply02[] = { 0xa1, 0x21, 0x00, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x82, 0x02, 0x03, 0x48, 0x03, 0x02, 0xD4, 0xF0, 0x57, 0x8D, 0x74, 0x23, 0x03, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
static const uint8_t reply03[] = { 0xa1, 0x21, 0x05, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x80, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
static const uint8_t reply04[] = { 0xa1, 0x21, 0x06, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x83, 0x04, 0x2C, 0x01, 0x2C, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
static const uint8_t reply08[] = { 0xa1, 0x21, 0x02, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x80, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
static const uint8_t reply1010[] = { 0xa1, 0x21, 0x04, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x90, 0x10, 0x10, 0x80, 0x00, 0x00, 0x18, 0x00, 0x00 };
static const uint8_t reply1020[] = { 0xa1, 0x21, 0x57, 0x8E, 0x00, 0x00, 0x00, 0x9A, 0x77, 0x8C, 0x00, 0x00, 0x00, 0x90, 0x90, 0x10, 0x20, 0x60, 0x00, 0x00, 0x18, 0x8c, 0x00, 0x31, 0x00, 0xDC, 0x00, 0x00, 0x40, 0x00, 0x40, 0x00, 0x40, 0xEF, 0xFF, 0xEB, 0xFF, 0xD2, 0xFF, 0x3B, 0x34, 0x3B, 0x34, 0x3B, 0x34, 0x00, 0x00, 0x00, 0x00, 0x00 };
static const uint8_t reply103D[] = { 0xa1, 0x21, 0x05, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x90, 0x10, 0x3D, 0x60, 0x00, 0x00, 0x19, 0x07, 0xD6, 0x65, 0xF7, 0x97, 0x7F, 0x81, 0x06, 0x5c, 0x1f, 0x78, 0x77, 0x4e, 0x56, 0x5f, 0xb8, 0xd5, 0x65, 0xFF, 0x2D, 0x2D, 0x2D, 0xE6, 0xE6, 0xE6, 0x00, 0x00, 0x00, 0x00 };
// コントローラ/ボタン/グリップのカラーを含むため send_padcolor() で書き換わる
static uint8_t reply1050[] = { 0xa1, 0x21, 0x04, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x90, 0x10, 0x50, 0x60, 0x00, 0x00, 0x18, 0x00, 0x95, 0xd9, 0xe6, 0x00, 0x33, 0xff, 0xd9, 0x00, 0x3e, 0xb3, 0x70, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
static const uint8_t reply1060[] = { 0xa1, 0x21, 0x03, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x90, 0x10, 0x00, 0x60, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
static const uint8_t reply1080[] = { 0xa1, 0x21, 0x04, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x90, 0x10, 0x80, 0x60, 0x00, 0x00, 0x18, 0x50, 0xFD, 0x00, 0x00, 0xC6, 0x0f, 0x0f, 0x30, 0x61, 0xAE, 0x90, 0xD9, 0xD4, 0x14, 0x54, 0x41, 0x15, 0x54, 0xC7, 0x79, 0x9C, 0x33, 0x36, 0x63, 0x00, 0x00 };
static const uint8_t reply1098[] = { 0xa1, 0x21, 0x04, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x90, 0x10, 0x98, 0x60, 0x00, 0x00, 0x12, 0x0f, 0x30, 0x61, 0xAE, 0x90, 0xd9, 0xd4, 0x14, 0x54, 0x41, 0x15, 0x54, 0xc7, 0x79, 0x9c, 0x33, 0x36, 0x63 };
static const uint8_t reply3001[] = { 0xa1, 0x21, 0x04, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x80, 0x30, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
static const uint8_t reply4001[] = { 0xa1, 0x21, 0x04, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x80, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
static const uint8_t reply4801[] = { 0xa1, 0x21, 0x04, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x80, 0x48, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
static const uint8_t reply2121[] = { 0xA1, 0x21, 0x00, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0xA0, 0x21, 0x01, 0x00, 0xFF, 0x00, 0x08, 0x00, 0x1B, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC8 };

// ペアリング (サブコマンド 0x01) の3段階の応答
// offset 17..22 の Bluetooth アドレスは update_bd_addr_in_replies() で実アドレスに差し替わる
static uint8_t reply01_1[] = { 0xa1, 0x21, 0x04, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x81, 0x01, 0x01, 0xD4, 0xF0, 0x57, 0x8D, 0x74, 0x23, 0x00, 0x25, 0x08, 0x50, 0x72, 0x6F, 0x20, 0x43, 0x6F, 0x6E, 0x74, 0x72, 0x6F, 0x6C, 0x6C, 0x65, 0x72, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x68, 0x00, 0x00, 0x00 };
static const uint8_t reply01_2[] = { 0xa1, 0x21, 0x04, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x81, 0x01, 0x02, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
static const uint8_t reply01_3[] = { 0xa1, 0x21, 0x04, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x81, 0x01, 0x03 };

#if BTKEYLIB_ENABLE_AMIIBO
static const uint8_t reply0331[] = { 0xa1, 0x21, 0x00, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x80, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
static const uint8_t reply2201[] = { 0xa1, 0x21, 0x00, 0x8E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x80, 0x00, 0x08, 0x80, 0x00, 0x80, 0x22, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
// NFC/IR MCU の状態を伝える 312 byte ペイロード
static const uint8_t reply1101[NFC_PAYLOAD_SIZE] = { 0x01, 0x00, 0x00, 0x00, 0x08, 0x00, 0x1B, 0x04 };
static const uint8_t reply11011[NFC_PAYLOAD_SIZE] = { 0x01, 0x00, 0x00, 0x00, 0x08, 0x00, 0x1B, 0x01 };
static const uint8_t replyFF[NFC_PAYLOAD_SIZE] = { 0xFF };
static const uint8_t reply110204[NFC_PAYLOAD_SIZE] = { 0x2A, 0x00, 0x05, 0x00, 0x00, 0x09, 0x31 };
// タグ検出通知。offset 16 以降に amiibo の UID が入るため send_amiibo() で書き換わる
static uint8_t reply110201[NFC_PAYLOAD_SIZE] = { 0x2A, 0x00, 0x05, 0x00, 0x00, 0x09, 0x31, 0x09, 0x00, 0x00, 0x00, 0x01, 0x01, 0x02, 0x00, 0x07 };
#define NFC_TAG_DETECTED_UID_OFFSET 16
#endif

typedef struct {
    const uint8_t * data;
    uint32_t        size;
} swpacket;

//----------------------------------------------------------
// ステートマシン
//
// pairing_state は「次に can-send-now が来たときに何を送るか」を表す。
// STATE_SUBCOMMAND_REPLY_FIRST .. _LAST は subcommand_replies[] の
// インデックスでもあるため、値と並びを変えてはならない。
//----------------------------------------------------------
typedef enum {
    STATE_INPUT_REPORT = 0,       // 応答待ちなし: 通常の入力レポートを流す

    STATE_SUBCOMMAND_REPLY_FIRST = 1,
    STATE_REPLY_02 = STATE_SUBCOMMAND_REPLY_FIRST,  // デバイス情報
    STATE_REPLY_08,               // 低電力モード設定
    STATE_REPLY_1060,             // SPI read 0x6000
    STATE_REPLY_1050,             // SPI read 0x6050 (カラー)
    STATE_REPLY_03,               // 入力レポートモード設定
    STATE_REPLY_04,               // トリガーボタン経過時間
    STATE_REPLY_1080,             // SPI read 0x6080
    STATE_REPLY_1098,             // SPI read 0x6098
    STATE_REPLY_1010,             // SPI read 0x8010
    STATE_REPLY_103D,             // SPI read 0x603D
    STATE_REPLY_1020,             // SPI read 0x6020
    STATE_REPLY_4001,             // 6軸センサ有効化
    STATE_REPLY_4801,             // 振動有効化
    STATE_REPLY_3001,             // プレイヤーランプ設定 → ペアリング完了
    STATE_REPLY_2121,             // NFC/IR MCU 状態
    STATE_SUBCOMMAND_REPLY_LAST = STATE_REPLY_2121,

    // 以降は amiibo (NFC) とペアリング情報の応答
    STATE_NFC_MCU_RESET = 16,     // reply0331
    STATE_NFC_IDLE,               // replyFF
    STATE_NFC_MCU_ON_A,           // reply2201 → STATE_NFC_TAG_READ
    STATE_NFC_TAG_DETECTED,       // reply110201 (UID 通知)
    STATE_NFC_TAG_DATA_1,         // amiibo 先頭ブロック → _DATA_2
    STATE_NFC_MCU_STATUS_A,       // reply2121 → _MCU_STATUS_B (未使用)
    STATE_NFC_TAG_READ,           // reply11011
    STATE_NFC_MCU_STATUS_B,       // reply2121 → STATE_NFC_TAG_PENDING
    STATE_NFC_TAG_POLLING,        // reply110204
    STATE_NFC_TAG_DATA_2,         // amiibo 中間ブロック → _DATA_3
    STATE_NFC_TAG_DATA_3,         // amiibo 末尾ブロック → _TAG_DETECTED
    STATE_NFC_TAG_PENDING,        // reply1101
    STATE_NFC_MCU_OFF,            // reply03 → STATE_INPUT_REPORT
    STATE_NFC_MCU_ON_B,           // reply2201 → STATE_INPUT_REPORT
    STATE_PAIRING_INFO_1,         // reply01_1
    STATE_PAIRING_INFO_2,         // reply01_2
    STATE_PAIRING_INFO_3,         // reply01_3
} pairing_state_t;

// インデックスは pairing_state_t の値と一致していること。
// 先頭 (STATE_INPUT_REPORT) は入力レポートを送るため参照されない。
static const swpacket subcommand_replies[STATE_SUBCOMMAND_REPLY_LAST + 1] = {
    {reply00,   sizeof(reply00)},
    {reply02,   sizeof(reply02)},
    {reply08,   sizeof(reply08)},
    {reply1060, sizeof(reply1060)},
    {reply1050, sizeof(reply1050)},
    {reply03,   sizeof(reply03)},
    {reply04,   sizeof(reply04)},
    {reply1080, sizeof(reply1080)},
    {reply1098, sizeof(reply1098)},
    {reply1010, sizeof(reply1010)},
    {reply103D, sizeof(reply103D)},
    {reply1020, sizeof(reply1020)},
    {reply4001, sizeof(reply4001)},
    {reply4801, sizeof(reply4801)},
    {reply3001, sizeof(reply3001)},
    {reply2121, sizeof(reply2121)}
};

//----------------------------------------------------------
// 3. amiibo (NFC) データ
//----------------------------------------------------------
#if BTKEYLIB_ENABLE_AMIIBO

#define AMIIBO_DUMP_SIZE   540
#define AMIIBO_UID_SIZE      7
#define AMIIBO_CHUNK1_SIZE 245
#define AMIIBO_CHUNK2_SIZE 295

static const uint8_t nfc_packet1[] = { 0x3a, 0x00, 0x07, 0x01, 0x00, 0x01, 0x31, 0x02, 0x00, 0x00, 0x00, 0x01, 0x02, 0x00, 0x07 };
static const uint8_t nfc_packet2[] = { 0x00, 0x00, 0x00, 0x00, 0x7D, 0xFD, 0xF0, 0x79, 0x36, 0x51, 0xAB, 0xD7, 0x46, 0x6E, 0x39, 0xC1, 0x91, 0xBA, 0xBE, 0xB8, 0x56, 0xCE, 0xED, 0xF1, 0xCE, 0x44, 0xCC, 0x75, 0xEA, 0xFB, 0x27, 0x09, 0x4D, 0x08, 0x7A, 0xE8, 0x03, 0x00, 0x3B, 0x3C, 0x77, 0x78, 0x86, 0x00, 0x00 };
static const uint8_t nfc_packet3[] = { 0x3a, 0x00, 0x07, 0x02, 0x00, 0x09, 0x27 };
static const uint8_t nfc_packet4[] = { 0x2a, 0x00, 0x05, 0x00, 0x00, 0x09, 0x31, 0x04, 0x00, 0x00, 0x00, 0x01, 0x01, 0x02, 0x00, 0x07 };

// ブロックが NFC ペイロードに収まることを確認する (C99 のため typedef で代用)
typedef char amiibo_block_size_check[
    ((sizeof(nfc_packet1) + AMIIBO_UID_SIZE + sizeof(nfc_packet2) + AMIIBO_CHUNK1_SIZE) <= NFC_PAYLOAD_SIZE)
    && ((sizeof(nfc_packet3) + AMIIBO_CHUNK2_SIZE) <= NFC_PAYLOAD_SIZE)
    && ((sizeof(nfc_packet4) + AMIIBO_UID_SIZE) <= NFC_PAYLOAD_SIZE)
    && ((AMIIBO_CHUNK1_SIZE + AMIIBO_CHUNK2_SIZE) == AMIIBO_DUMP_SIZE) ? 1 : -1];

// Switch へ送る 3 分割された amiibo データと、その CRC8
static uint8_t amiibo_block1[NFC_PAYLOAD_SIZE];
static uint8_t amiibo_block2[NFC_PAYLOAD_SIZE];
static uint8_t amiibo_block3[NFC_PAYLOAD_SIZE];
static uint8_t crc_block1, crc_block2, crc_block3, crc_tag_detected;

static const uint8_t CRC_TABLE[256] = {
    0x00, 0x07, 0x0E, 0x09, 0x1C, 0x1B, 0x12, 0x15,
    0x38, 0x3F, 0x36, 0x31, 0x24, 0x23, 0x2A, 0x2D,
    0x70, 0x77, 0x7E, 0x79, 0x6C, 0x6B, 0x62, 0x65,
    0x48, 0x4F, 0x46, 0x41, 0x54, 0x53, 0x5A, 0x5D,
    0xE0, 0xE7, 0xEE, 0xE9, 0xFC, 0xFB, 0xF2, 0xF5,
    0xD8, 0xDF, 0xD6, 0xD1, 0xC4, 0xC3, 0xCA, 0xCD,
    0x90, 0x97, 0x9E, 0x99, 0x8C, 0x8B, 0x82, 0x85,
    0xA8, 0xAF, 0xA6, 0xA1, 0xB4, 0xB3, 0xBA, 0xBD,
    0xC7, 0xC0, 0xC9, 0xCE, 0xDB, 0xDC, 0xD5, 0xD2,
    0xFF, 0xF8, 0xF1, 0xF6, 0xE3, 0xE4, 0xED, 0xEA,
    0xB7, 0xB0, 0xB9, 0xBE, 0xAB, 0xAC, 0xA5, 0xA2,
    0x8F, 0x88, 0x81, 0x86, 0x93, 0x94, 0x9D, 0x9A,
    0x27, 0x20, 0x29, 0x2E, 0x3B, 0x3C, 0x35, 0x32,
    0x1F, 0x18, 0x11, 0x16, 0x03, 0x04, 0x0D, 0x0A,
    0x57, 0x50, 0x59, 0x5E, 0x4B, 0x4C, 0x45, 0x42,
    0x6F, 0x68, 0x61, 0x66, 0x73, 0x74, 0x7D, 0x7A,
    0x89, 0x8E, 0x87, 0x80, 0x95, 0x92, 0x9B, 0x9C,
    0xB1, 0xB6, 0xBF, 0xB8, 0xAD, 0xAA, 0xA3, 0xA4,
    0xF9, 0xFE, 0xF7, 0xF0, 0xE5, 0xE2, 0xEB, 0xEC,
    0xC1, 0xC6, 0xCF, 0xC8, 0xDD, 0xDA, 0xD3, 0xD4,
    0x69, 0x6E, 0x67, 0x60, 0x75, 0x72, 0x7B, 0x7C,
    0x51, 0x56, 0x5F, 0x58, 0x4D, 0x4A, 0x43, 0x44,
    0x19, 0x1E, 0x17, 0x10, 0x05, 0x02, 0x0B, 0x0C,
    0x21, 0x26, 0x2F, 0x28, 0x3D, 0x3A, 0x33, 0x34,
    0x4E, 0x49, 0x40, 0x47, 0x52, 0x55, 0x5C, 0x5B,
    0x76, 0x71, 0x78, 0x7F, 0x6A, 0x6D, 0x64, 0x63,
    0x3E, 0x39, 0x30, 0x37, 0x22, 0x25, 0x2C, 0x2B,
    0x06, 0x01, 0x08, 0x0F, 0x1A, 0x1D, 0x14, 0x13,
    0xAE, 0xA9, 0xA0, 0xA7, 0xB2, 0xB5, 0xBC, 0xBB,
    0x96, 0x91, 0x98, 0x9F, 0x8A, 0x8D, 0x84, 0x83,
    0xDE, 0xD9, 0xD0, 0xD7, 0xC2, 0xC5, 0xCC, 0xCB,
    0xE6, 0xE1, 0xE8, 0xEF, 0xFA, 0xFD, 0xF4, 0xF3
};

static uint8_t crc8ccitt(const void * data, size_t size){
    const uint8_t * pos = (const uint8_t *) data;
    const uint8_t * end = pos + size;
    uint8_t val = 0;

    while (pos < end){
        val = CRC_TABLE[val ^ *pos];
        pos++;
    }
    return val;
}

/**
 * NTAG215 のダンプから UID を取り出す。
 * page0 の 3 byte + page1 の 4 byte で構成され、page0 の 4 byte 目 (BCC0) は含まない。
 */
static void amiibo_extract_uid(const uint8_t * dump, uint8_t * uid){
    memcpy(&uid[0], &dump[0], 3);
    memcpy(&uid[3], &dump[4], 4);
}

/**
 * ダンプから Switch へ送る 3 ブロックを組み立てる。
 * block1: nfc_packet1 + UID + nfc_packet2 + ダンプ前半 (245 byte) = 312 byte
 * block2: nfc_packet3 + ダンプ後半 (295 byte)
 * block3: nfc_packet4 + UID
 */
static void amiibo_build_blocks(const uint8_t * dump, const uint8_t * uid){
    uint16_t pos;

    memset(amiibo_block1, 0, sizeof(amiibo_block1));
    pos = 0;
    memcpy(&amiibo_block1[pos], nfc_packet1, sizeof(nfc_packet1));
    pos += sizeof(nfc_packet1);
    memcpy(&amiibo_block1[pos], uid, AMIIBO_UID_SIZE);
    pos += AMIIBO_UID_SIZE;
    memcpy(&amiibo_block1[pos], nfc_packet2, sizeof(nfc_packet2));
    pos += sizeof(nfc_packet2);
    memcpy(&amiibo_block1[pos], &dump[0], AMIIBO_CHUNK1_SIZE);

    memset(amiibo_block2, 0, sizeof(amiibo_block2));
    pos = 0;
    memcpy(&amiibo_block2[pos], nfc_packet3, sizeof(nfc_packet3));
    pos += sizeof(nfc_packet3);
    memcpy(&amiibo_block2[pos], &dump[AMIIBO_CHUNK1_SIZE], AMIIBO_CHUNK2_SIZE);

    memset(amiibo_block3, 0, sizeof(amiibo_block3));
    pos = 0;
    memcpy(&amiibo_block3[pos], nfc_packet4, sizeof(nfc_packet4));
    pos += sizeof(nfc_packet4);
    memcpy(&amiibo_block3[pos], uid, AMIIBO_UID_SIZE);
}

void BTKEYLIB_API send_amiibo(const char * path){
    uint8_t dump[AMIIBO_DUMP_SIZE];
    uint8_t uid[AMIIBO_UID_SIZE];
    FILE * fp;

    // 532 byte のダンプも扱えるよう、読み込めなかった分は 0 のままにする
    memset(dump, 0, sizeof(dump));

#ifdef _WIN32
    if (fopen_s(&fp, path, "rb") != 0) return;
#else
    fp = fopen(path, "rb");
    if (fp == NULL) return;
#endif
    (void) fread(dump, sizeof(uint8_t), sizeof(dump), fp);
    fclose(fp);

    amiibo_extract_uid(dump, uid);
    memcpy(&reply110201[NFC_TAG_DETECTED_UID_OFFSET], uid, AMIIBO_UID_SIZE);
    amiibo_build_blocks(dump, uid);

    crc_block1 = crc8ccitt(amiibo_block1, sizeof(amiibo_block1));
    crc_block2 = crc8ccitt(amiibo_block2, sizeof(amiibo_block2));
    crc_block3 = crc8ccitt(amiibo_block3, sizeof(amiibo_block3));
    crc_tag_detected = crc8ccitt(reply110201, sizeof(reply110201));
}
#endif

//----------------------------------------------------------
// 4. コントローラ状態と公開 API
//----------------------------------------------------------
static btstack_packet_callback_registration_t hci_event_callback_registration;
static uint16_t hid_cid;
static pairing_state_t pairing_state = STATE_INPUT_REPORT;
// 一度でもハンドシェイクを完走したか。再接続時のショートカット判定に使うため、
// 切断されてもクリアしない (クリアすると再接続時に 0x48/0x21 の応答から復帰できない)
static bool handshake_done = false;

// Switch から受信した出力レポートのタイマ値。応答パケットにそのまま反映する
static uint8_t report_counter = 0;
// 入力レポートに載せるタイマ値
static uint8_t input_report_timer = 0;

static uint8_t hid_service_buffer[900];
static uint8_t device_id_sdp_service_buffer[400];
static uint8_t l2cap_sdp_service_buffer[600];

#if BTKEYLIB_ENABLE_AMIIBO
static bool packet_amiibo_flag = false;
#endif
#if BTKEYLIB_ENABLE_RUMBLE
static bool rumble_flag = false;
static uint32_t rumble_bflag = 0;
#endif

static uint32_t button_flg = 0;
static uint32_t stick_r_flg = 0x800800;
static uint32_t stick_l_flg = 0x800800;
static int16_t accel_x_flg = 100;
static int16_t accel_y_flg = 100;
static int16_t accel_z_flg = 100;
static int16_t gyro1_flg = 0;
static int16_t gyro2_flg = 0;
static int16_t gyro3_flg = 0;

bool BTKEYLIB_API gamepad_paired(void){
    // 「今 Switch と通信できる状態か」= HID 接続あり かつ ハンドシェイク完了済み
    return handshake_done && (hid_cid != 0);
}

void BTKEYLIB_API send_button(uint32_t button_status, uint32_t press_time){
    UNUSED(press_time);
    button_flg = button_status;
}

void BTKEYLIB_API send_stick_r(uint32_t stick_horizontal, uint32_t stick_vertical, uint32_t press_time){
    UNUSED(press_time);
    stick_r_flg = stick_horizontal | (stick_vertical << 12);
}

void BTKEYLIB_API send_stick_l(uint32_t stick_horizontal, uint32_t stick_vertical, uint32_t press_time){
    UNUSED(press_time);
    stick_l_flg = stick_horizontal | (stick_vertical << 12);
}

void BTKEYLIB_API send_gyro(int16_t gyro1, int16_t gyro2, int16_t gyro3){
    gyro1_flg = gyro1;
    gyro2_flg = gyro2;
    gyro3_flg = gyro3;
}

void BTKEYLIB_API send_accel(int16_t accel_x, int16_t accel_y, int16_t accel_z){
    accel_x_flg = accel_x;
    accel_y_flg = accel_y;
    accel_z_flg = accel_z;
}

void BTKEYLIB_API send_padcolor(uint32_t pad_color, uint32_t button_color, uint32_t leftgrip_color,
                                uint32_t rightgrip_color){
    // SPI 0x6050 の応答は BGR 順で 本体/ボタン/左グリップ/右グリップ の 4 色が並ぶ
    const uint32_t colors[4] = { pad_color, button_color, leftgrip_color, rightgrip_color };
    int i;

    for (i = 0; i < 4; i++){
        uint8_t * dst = &reply1050[SPI_COLOR_OFFSET + (i * 3)];
        dst[0] = (colors[i] >> 0)  & 0xFF;  // B
        dst[1] = (colors[i] >> 8)  & 0xFF;  // G
        dst[2] = (colors[i] >> 16) & 0xFF;  // R
    }
}

#if BTKEYLIB_ENABLE_RUMBLE
bool BTKEYLIB_API get_rumble(void){
    return rumble_flag;
}

void BTKEYLIB_API rumble_register(uint32_t key){
    rumble_bflag = key;
}
#endif

//----------------------------------------------------------
// 5. 入力レポートの生成と送信
//----------------------------------------------------------
static void sleep_seconds(double seconds){
#ifdef _WIN32
    Sleep((DWORD)(seconds * 1000));
#else
    usleep((useconds_t)(seconds * 1000000));
#endif
}

/**
 * 6軸センサ 1 サンプル分を書き込む。Switch は 1 レポートあたり 5ms 間隔の
 * 3 サンプルを期待するが、ここでは最新値を 3 回繰り返す。
 */
static void build_imu_frame(uint8_t * frame){
    little_endian_store_16(frame,  0, (uint16_t) accel_x_flg);
    little_endian_store_16(frame,  2, (uint16_t) accel_y_flg);
    little_endian_store_16(frame,  4, (uint16_t) accel_z_flg);
    little_endian_store_16(frame,  6, (uint16_t) gyro1_flg);
    little_endian_store_16(frame,  8, (uint16_t) gyro2_flg);
    little_endian_store_16(frame, 10, (uint16_t) gyro3_flg);
}

/**
 * 入力レポート (0x30 / 0x31) の先頭 INPUT_REPORT_SIZE byte を組み立てる
 */
static void build_input_report(uint8_t * report, uint8_t report_id){
    uint32_t buttons = button_flg;
    uint8_t i;

#if BTKEYLIB_ENABLE_RUMBLE
    // 振動を受信している間は登録されたボタンを押下扱いにする
    if (rumble_flag && (rumble_bflag != 0)){
        buttons |= rumble_bflag;
    }
#endif

    report[0] = HID_INPUT_REPORT_PREFIX;
    report[1] = report_id;
    report[2] = ++input_report_timer;
    report[3] = BATTERY_CONNECTION_INFO;

    report[4] = (buttons >> 0)  & 0xFF;
    report[5] = (buttons >> 8)  & 0xFF;
    report[6] = (buttons >> 16) & 0xFF;

    report[7] = (stick_l_flg >> 0)  & 0xFF;
    report[8] = (stick_l_flg >> 8)  & 0xFF;
    report[9] = (stick_l_flg >> 16) & 0xFF;

    report[10] = (stick_r_flg >> 0)  & 0xFF;
    report[11] = (stick_r_flg >> 8)  & 0xFF;
    report[12] = (stick_r_flg >> 16) & 0xFF;

    report[13] = 0x00;  // 振動レポート

    for (i = 0; i < IMU_FRAMES_PER_REPORT; i++){
        build_imu_frame(&report[IMU_FRAME_OFFSET + (i * IMU_FRAME_SIZE)]);
    }
}

/**
 * 保持されているボタンフラグを Switch 本体へ送信する
 */
static void send_input_report(void){
    static clock_t last_sent = 0;
    uint8_t report[INPUT_REPORT_SIZE];
    double elapsed;

    build_input_report(report, INPUT_REPORT_ID_STANDARD);

    // 60Hz を超えないように前回送信からの経過時間分を差し引いて待つ
    elapsed = (double)(clock() - last_sent) / CLOCKS_PER_SEC;
    if (elapsed < INPUT_REPORT_INTERVAL_S){
        sleep_seconds(INPUT_REPORT_INTERVAL_S - elapsed);
    }
    hid_device_send_interrupt_message(hid_cid, report, sizeof(report));
    last_sent = clock();
}

#if BTKEYLIB_ENABLE_AMIIBO
/**
 * 保持されているボタンフラグ + NFC ペイロードを Switch 本体へ送信する
 */
static void send_input_report_with_nfc(const uint8_t * payload, uint8_t crc){
    uint8_t report[INPUT_REPORT_SIZE + NFC_PAYLOAD_SIZE + 1];

    build_input_report(report, INPUT_REPORT_ID_NFC_IR);
    memcpy(&report[INPUT_REPORT_SIZE], payload, NFC_PAYLOAD_SIZE);
    report[INPUT_REPORT_SIZE + NFC_PAYLOAD_SIZE] = crc;

    hid_device_send_interrupt_message(hid_cid, report, sizeof(report));
}
#endif

/**
 * 定型応答パケットを送信する。長さによらず常に SUBCOMMAND_REPLY_SIZE byte 送る
 */
static void send_subcommand_reply(const uint8_t * reply, uint32_t reply_len){
    uint8_t buffer[SUBCOMMAND_REPLY_SIZE] = { 0 };

    btstack_assert(reply_len <= sizeof(buffer));
    memcpy(buffer, reply, reply_len);
    buffer[2] = ++report_counter;
    hid_device_send_interrupt_message(hid_cid, buffer, sizeof(buffer));
}

//----------------------------------------------------------
// 6. Switch 本体からの受信パケット解析
//----------------------------------------------------------

/**
 * 範囲外アクセスを避けつつ出力レポートの 1 byte を読む。
 * 振動のみのレポート (0x10) はサブコマンド領域を持たないため短い。
 */
static uint8_t report_byte(const uint8_t * report, int report_size, int index){
    return (index < report_size) ? report[index] : 0;
}

#if BTKEYLIB_ENABLE_RUMBLE
static void update_rumble_state(uint16_t report_id, const uint8_t * report, int report_size){
    if ((report_id == OUT_REPORT_ID_RUMBLE) && (report_size > 6)
        && (report[1] != 0) && (report[2] != 1) && (report[5] != 0) && (report[6] != 1)){
        rumble_flag = true;
    } else {
        rumble_flag = false;
    }
}
#endif

#if BTKEYLIB_ENABLE_AMIIBO
// サブコマンド 0x02 (デバイス情報要求 / NFC MCU 制御) の処理
static void handle_subcommand_02(uint8_t arg0){
    switch (arg0){
        case 1:
            packet_amiibo_flag = true;
            pairing_state = STATE_NFC_TAG_DETECTED;
            break;
        case 2:
            packet_amiibo_flag = false;
            pairing_state = STATE_NFC_TAG_POLLING;
            break;
        case 4:
            // amiibo 転送中はステートを進めない
            if (!packet_amiibo_flag){
                pairing_state = STATE_NFC_TAG_POLLING;
            }
            break;
        case 6:
            packet_amiibo_flag = true;
            pairing_state = STATE_NFC_TAG_DATA_1;
            break;
        default:
            pairing_state = STATE_REPLY_02;
            break;
    }
}

// サブコマンド 0x03 (入力レポートモード設定) の処理
static void handle_subcommand_03(uint8_t arg0){
    if (arg0 == 0x31){
        pairing_state = STATE_NFC_MCU_RESET;
    } else if ((arg0 == 0x30) && (pairing_state == STATE_NFC_TAG_READ)){
        packet_amiibo_flag = false;
        pairing_state = STATE_NFC_MCU_OFF;
    } else {
        pairing_state = STATE_REPLY_03;
    }
}
#endif

// サブコマンド 0x10 (SPI flash read) の処理
static void handle_subcommand_spi_read(uint16_t address){
    switch (address){
        case 0x6000:
            pairing_state = STATE_REPLY_1060;
            break;
        case 0x6020:
            pairing_state = STATE_REPLY_1020;
            break;
        case 0x603D:
            pairing_state = STATE_REPLY_103D;
            break;
        case 0x6050:
            pairing_state = STATE_REPLY_1050;
            break;
        case 0x6080:
            pairing_state = STATE_REPLY_1080;
            break;
        case 0x6098:
            pairing_state = STATE_REPLY_1098;
            break;
        case 0x8010:
            pairing_state = STATE_REPLY_1010;
            break;
        default:
            // 0x8028 (6軸センサ) など、応答を用意していないアドレス
            break;
    }
}

static void handle_subcommand(uint16_t report_id, const uint8_t * report, int report_size){
    uint8_t subcommand;
    uint8_t arg0;

    // サブコマンド ID を含まない短いレポート (振動のみ等) は対象外
    if (report_size <= OUT_REPORT_SUBCMD) return;

    subcommand = report[OUT_REPORT_SUBCMD];
    arg0 = report_byte(report, report_size, OUT_REPORT_ARG(0));

    switch (subcommand){
        case 0x01:
            // Bluetooth 手動ペアリング: 3 段階のやり取りを行う
            switch (arg0){
                case 1:
                    pairing_state = STATE_PAIRING_INFO_1;
                    break;
                case 2:
                    pairing_state = STATE_PAIRING_INFO_2;
                    break;
                case 3:
                    pairing_state = STATE_PAIRING_INFO_3;
                    break;
                default:
                    break;
            }
            break;

        case 0x02:
#if BTKEYLIB_ENABLE_AMIIBO
            handle_subcommand_02(arg0);
#else
            pairing_state = STATE_REPLY_02;
#endif
            break;

        case 0x03:
#if BTKEYLIB_ENABLE_AMIIBO
            handle_subcommand_03(arg0);
#else
            pairing_state = STATE_REPLY_03;
#endif
            break;

        case 0x04:
            pairing_state = STATE_REPLY_04;
            break;

        case 0x08:
            pairing_state = STATE_REPLY_08;
            break;

        case 0x10:
            handle_subcommand_spi_read((uint16_t)((report_byte(report, report_size, OUT_REPORT_ARG(1)) << 8) | arg0));
            break;

        case 0x21:
            if (arg0 != 0x21) break;
#if BTKEYLIB_ENABLE_AMIIBO
            if (report_byte(report, report_size, OUT_REPORT_ARG(2)) == 4){
                pairing_state = STATE_NFC_MCU_STATUS_B;
            } else {
                pairing_state = STATE_REPLY_2121;
            }
#else
            pairing_state = STATE_REPLY_2121;
#endif
            break;

#if BTKEYLIB_ENABLE_AMIIBO
        case 0x22:
            if ((report_size == 48) && (arg0 == 1)){
                pairing_state = (pairing_state > STATE_SUBCOMMAND_REPLY_LAST)
                              ? STATE_NFC_MCU_ON_A
                              : STATE_NFC_MCU_ON_B;
            }
            break;
#endif

        case 0x30:
            if (report_id == OUT_REPORT_ID_SUBCOMMAND){
                pairing_state = STATE_REPLY_3001;
            }
            break;

        case 0x40:
            pairing_state = STATE_REPLY_4001;
            break;

        case 0x48:
            pairing_state = STATE_REPLY_4801;
            break;

        default:
            break;
    }
}

static void hid_report_data_callback(uint16_t cid, hid_report_type_t report_type, uint16_t report_id,
                                     int report_size, uint8_t * report){
    UNUSED(cid);
    UNUSED(report_type);

    if (report_size <= OUT_REPORT_TIMER) return;

    report_counter = report[OUT_REPORT_TIMER];

#if BTKEYLIB_ENABLE_RUMBLE
    update_rumble_state(report_id, report, report_size);
#endif
    handle_subcommand(report_id, report, report_size);

    hid_device_request_can_send_now_event(hid_cid);
}

//----------------------------------------------------------
// 7. can-send-now ハンドラ
//----------------------------------------------------------
static void handle_can_send_now(void){
    if (pairing_state == STATE_INPUT_REPORT){
        send_input_report();
        return;
    }

    if (pairing_state <= STATE_SUBCOMMAND_REPLY_LAST){
        const swpacket * reply = &subcommand_replies[pairing_state];

        if (pairing_state == STATE_REPLY_3001){
            // プレイヤーランプ設定への応答をもってハンドシェイク完了
            pairing_state = STATE_INPUT_REPORT;
            input_report_timer = ++report_counter;
            handshake_done = true;
        } else if (handshake_done && ((pairing_state == STATE_REPLY_4801) || (pairing_state == STATE_REPLY_2121))){
            // ペアリング後の情報要求に応答したら通常動作に戻す
            pairing_state = STATE_INPUT_REPORT;
        }
        send_subcommand_reply(reply->data, reply->size);
        return;
    }

    switch (pairing_state){
#if BTKEYLIB_ENABLE_AMIIBO
        case STATE_NFC_MCU_RESET:
            pairing_state = STATE_NFC_IDLE;
            send_subcommand_reply(reply0331, sizeof(reply0331));
            break;
        case STATE_NFC_IDLE:
            send_input_report_with_nfc(replyFF, 0x6F);
            break;
        case STATE_NFC_MCU_ON_A:
            pairing_state = STATE_NFC_TAG_READ;
            send_subcommand_reply(reply2201, sizeof(reply2201));
            break;
        case STATE_NFC_TAG_DETECTED:
            send_input_report_with_nfc(reply110201, crc_tag_detected);
            break;
        case STATE_NFC_TAG_DATA_1:
            send_input_report_with_nfc(amiibo_block1, crc_block1);
            pairing_state = STATE_NFC_TAG_DATA_2;
            break;
        case STATE_NFC_MCU_STATUS_A:
            pairing_state = STATE_NFC_MCU_STATUS_B;
            send_subcommand_reply(reply2121, sizeof(reply2121));
            break;
        case STATE_NFC_TAG_READ:
            send_input_report_with_nfc(reply11011, 0x13);
            break;
        case STATE_NFC_MCU_STATUS_B:
            pairing_state = STATE_NFC_TAG_PENDING;
            send_subcommand_reply(reply2121, sizeof(reply2121));
            break;
        case STATE_NFC_TAG_POLLING:
            send_input_report_with_nfc(reply110204, 0x0F);
            break;
        case STATE_NFC_TAG_DATA_2:
            send_input_report_with_nfc(amiibo_block2, crc_block2);
            pairing_state = STATE_NFC_TAG_DATA_3;
            break;
        case STATE_NFC_TAG_DATA_3:
            send_input_report_with_nfc(amiibo_block3, crc_block3);
            pairing_state = STATE_NFC_TAG_DETECTED;
            break;
        case STATE_NFC_TAG_PENDING:
            send_input_report_with_nfc(reply1101, 0x01);
            break;
        case STATE_NFC_MCU_OFF:
            pairing_state = STATE_INPUT_REPORT;
            send_subcommand_reply(reply03, sizeof(reply03));
            break;
        case STATE_NFC_MCU_ON_B:
            pairing_state = STATE_INPUT_REPORT;
            send_subcommand_reply(reply2201, sizeof(reply2201));
            break;
#endif
        case STATE_PAIRING_INFO_1:
            send_subcommand_reply(reply01_1, sizeof(reply01_1));
            break;
        case STATE_PAIRING_INFO_2:
            send_subcommand_reply(reply01_2, sizeof(reply01_2));
            break;
        case STATE_PAIRING_INFO_3:
            send_subcommand_reply(reply01_3, sizeof(reply01_3));
            break;
        default:
            break;
    }
}

/**
 * サブコマンド応答が申告する Bluetooth アドレスを、実際に使っているドングルの
 * アドレスに差し替える。実機と異なるアドレスを申告すると Switch 側が持つ登録情報と
 * 食い違うため、HCI が動き出した時点で一度だけ実行する。
 *
 * 0x01 / 0x02 応答のアドレスは上位バイトが先頭 (big endian) で、
 * btstack の bd_addr_t と同じ並びのためそのままコピーできる。
 */
static void update_bd_addr_in_replies(void){
    bd_addr_t local_addr;

    gap_local_bd_addr(local_addr);
    log_info("btkeyLib: reporting Bluetooth address %s", bd_addr_to_str(local_addr));

    memcpy(&reply01_1[REPLY01_BD_ADDR_OFFSET], local_addr, BD_ADDR_LEN);
    memcpy(&reply02[REPLY02_BD_ADDR_OFFSET], local_addr, BD_ADDR_LEN);
}

//----------------------------------------------------------
// デバイスイベント処理
//----------------------------------------------------------
static void nintendo_packet_handler(uint8_t packet_type, uint16_t channel, uint8_t * packet, uint16_t packet_size){
    UNUSED(channel);
    UNUSED(packet_size);

    if (packet_type != HCI_EVENT_PACKET) return;

    if (hci_event_packet_get_type(packet) == BTSTACK_EVENT_STATE){
        if (btstack_event_state_get_state(packet) == HCI_STATE_WORKING){
            update_bd_addr_in_replies();
        }
        return;
    }

    if (hci_event_packet_get_type(packet) != HCI_EVENT_HID_META) return;

    switch (hci_event_hid_meta_get_subevent_code(packet)){
        case HID_SUBEVENT_CONNECTION_OPENED:
            if (hid_subevent_connection_opened_get_status(packet) != ERROR_CODE_SUCCESS){
                hid_cid = 0;
                return;
            }
            hid_cid = hid_subevent_connection_opened_get_hid_cid(packet);
            hid_device_request_can_send_now_event(hid_cid);
            break;

        case HID_SUBEVENT_CONNECTION_CLOSED:
            // hid_cid = 0 により gamepad_paired() は false を返すようになる
            hid_cid = 0;
            pairing_state = STATE_INPUT_REPORT;
            break;

        case HID_SUBEVENT_CAN_SEND_NOW:
            handle_can_send_now();
            hid_device_request_can_send_now_event(hid_cid);
            break;

        default:
            break;
    }
}

//----------------------------------------------------------
// 8. Bluetoothデバイス情報のセットアップ
//----------------------------------------------------------
static void l2cap_create_sdp_record(void){
    uint8_t * attribute;
    de_create_sequence(l2cap_sdp_service_buffer);

    de_add_number(l2cap_sdp_service_buffer, DE_UINT, DE_SIZE_16, BLUETOOTH_ATTRIBUTE_SERVICE_RECORD_HANDLE);
    de_add_number(l2cap_sdp_service_buffer, DE_UINT, DE_SIZE_32, 0x0000);

    de_add_number(l2cap_sdp_service_buffer, DE_UINT, DE_SIZE_16, BLUETOOTH_ATTRIBUTE_SERVICE_CLASS_ID_LIST);
    attribute = de_push_sequence(l2cap_sdp_service_buffer);
    {
        de_add_number(attribute,  DE_UUID, DE_SIZE_16, 0x1000);
    }
    de_pop_sequence(l2cap_sdp_service_buffer, attribute);
    de_add_number(l2cap_sdp_service_buffer,  DE_UINT, DE_SIZE_16, BLUETOOTH_ATTRIBUTE_PROTOCOL_DESCRIPTOR_LIST);
    attribute = de_push_sequence(l2cap_sdp_service_buffer);
    {
        uint8_t * l2cpProtocol = de_push_sequence(attribute);
        {
            de_add_number(l2cpProtocol,  DE_UUID, DE_SIZE_16, BLUETOOTH_PROTOCOL_L2CAP);
            de_add_number(l2cpProtocol,  DE_UINT, DE_SIZE_16, 0x01);
        }
        de_pop_sequence(attribute, l2cpProtocol);

        uint8_t * hidProtocol = de_push_sequence(attribute);
        {
            de_add_number(hidProtocol,  DE_UUID, DE_SIZE_16, 0x01);
        }
        de_pop_sequence(attribute, hidProtocol);
    }
    de_pop_sequence(l2cap_sdp_service_buffer, attribute);
    de_add_number(l2cap_sdp_service_buffer,  DE_UINT, DE_SIZE_16, BLUETOOTH_ATTRIBUTE_BROWSE_GROUP_LIST);
    attribute = de_push_sequence(l2cap_sdp_service_buffer);
    {
        de_add_number(attribute,  DE_UUID, DE_SIZE_16, BLUETOOTH_ATTRIBUTE_PUBLIC_BROWSE_ROOT );
    }
    de_pop_sequence(l2cap_sdp_service_buffer, attribute);
    de_add_number(l2cap_sdp_service_buffer,  DE_UINT, DE_SIZE_16, BLUETOOTH_ATTRIBUTE_LANGUAGE_BASE_ATTRIBUTE_ID_LIST);
    attribute = de_push_sequence(l2cap_sdp_service_buffer);
    {
        de_add_number(attribute, DE_UINT, DE_SIZE_16, 0x656e);
        de_add_number(attribute, DE_UINT, DE_SIZE_16, 0x006a);
        de_add_number(attribute, DE_UINT, DE_SIZE_16, 0x0100);
    }
    de_pop_sequence(l2cap_sdp_service_buffer, attribute);
    de_add_number(l2cap_sdp_service_buffer,  DE_UINT, DE_SIZE_16, BLUETOOTH_ATTRIBUTE_BLUETOOTH_PROFILE_DESCRIPTOR_LIST);
    attribute = de_push_sequence(l2cap_sdp_service_buffer);
    {
        uint8_t * hidProfile = de_push_sequence(attribute);
        {
            de_add_number(hidProfile,  DE_UUID, DE_SIZE_16, BLUETOOTH_PROTOCOL_L2CAP);
            de_add_number(hidProfile,  DE_UINT, DE_SIZE_16, 0x0100);
        }
        de_pop_sequence(attribute, hidProfile);
    }
    de_pop_sequence(l2cap_sdp_service_buffer, attribute);
    const char wgp2[] = "Wireless Gamepad";
    de_add_number(l2cap_sdp_service_buffer,  DE_UINT, DE_SIZE_16, 0x0100);
    de_add_data(l2cap_sdp_service_buffer, DE_STRING, strlen(wgp2), (uint8_t *)wgp2);
    const char gp2[] = "Gamepad";
    de_add_number(l2cap_sdp_service_buffer,  DE_UINT, DE_SIZE_16, 0x0101);
    de_add_data(l2cap_sdp_service_buffer, DE_STRING, strlen(gp2), (uint8_t *)gp2);
    de_add_number(l2cap_sdp_service_buffer,  DE_UINT, DE_SIZE_16, BLUETOOTH_ATTRIBUTE_SPECIFICATION_ID);
    attribute = de_push_sequence(l2cap_sdp_service_buffer);
    {
        de_add_number(attribute,  DE_UINT, DE_SIZE_16, 0x0100);
    }
    de_pop_sequence(l2cap_sdp_service_buffer, attribute);

    sdp_register_service(l2cap_sdp_service_buffer);
}

//----------------------------------------------------------
// 起動処理
//----------------------------------------------------------
int btstack_main(int argc, const char * argv[]);
int btstack_main(int argc, const char * argv[]){
    UNUSED(argc);
    UNUSED(argv);

    gap_discoverable_control(1);
    gap_set_class_of_device(0x2508);
    gap_set_local_name(hid_device_name);
    gap_set_default_link_policy_settings(LM_LINK_POLICY_ENABLE_ROLE_SWITCH | LM_LINK_POLICY_ENABLE_SNIFF_MODE);
    gap_set_allow_role_switch(true);

    // BTstack v1.8.2 でデフォルトが変わった 2 つを、Pro Controller として動作していた
    // 頃の値に戻す。本ライブラリは UI を持たないため SSP の確認応答を自前で返せない
    //   - 自動 accept: 1 -> 0 になった。0 のままだと HCI_EVENT_USER_CONFIRMATION_REQUEST に
    //     誰も応答せず、Switch 側が認証タイムアウトで切断する
    //   - 最小暗号鍵長: 7 -> 16 になった。16 byte 未満で暗号化されたリンクは
    //     セキュリティレベル 0 扱いになる
    gap_ssp_set_auto_accept(1);
    gap_set_required_encryption_key_size(7);

    l2cap_init();
    sm_init();
    sdp_init();

    memset(hid_service_buffer, 0, sizeof(hid_service_buffer));
    const hid_sdp_record_t hid_params = {
        // hid service subclass 2508 Gamepad, hid country code 33 US
        0x08, 33,
        1,      // hid_virtual_cable
        1,      // hid_remote_wake
        1,      // hid_reconnect_initiate
        false,  // hid_normally_connectable
        false,  // hid_boot_device
        0x0640, 0x0320, 0x0c80,
        reportMap,
        sizeof(reportMap),
        hid_device_name
    };
    hid_create_sdp_record(hid_service_buffer, sdp_create_service_record_handle(), &hid_params);
    sdp_register_service(hid_service_buffer);

    device_id_create_sdp_record(device_id_sdp_service_buffer, sdp_create_service_record_handle(),
                                DEVICE_ID_VENDOR_ID_SOURCE_USB, 0x057e, 0x2009, 1);
    sdp_register_service(device_id_sdp_service_buffer);

    l2cap_create_sdp_record();

    hid_device_init(true, sizeof(reportMap), reportMap);
    // Switch はディスクリプタで宣言したサイズより短いサブコマンドレポートを送ってくる
    hid_device_accept_truncated_hid_reports(true);
    hid_device_register_packet_handler(&nintendo_packet_handler);
    hid_device_register_report_data_callback(&hid_report_data_callback);

    hci_event_callback_registration.callback = &nintendo_packet_handler;
    hci_add_event_handler(&hci_event_callback_registration);
    hci_register_sco_packet_handler(&nintendo_packet_handler);

    hci_power_control(HCI_POWER_ON);

    return 0;
}
