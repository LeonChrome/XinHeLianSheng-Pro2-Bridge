/*
 * Copyright (C) 2021 BlueKitchen GmbH
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 *
 * 1. Redistributions of source code must retain the above copyright
 *    notice, this list of conditions and the following disclaimer.
 * 2. Redistributions in binary form must reproduce the above copyright
 *    notice, this list of conditions and the following disclaimer in the
 *    documentation and/or other materials provided with the distribution.
 * 3. Neither the name of the copyright holders nor the names of
 *    contributors may be used to endorse or promote products derived
 *    from this software without specific prior written permission.
 * 4. Any redistribution, use, or modification is done solely for
 *    personal benefit and not for any commercial purpose or for
 *    monetary gain.
 *
 * THIS SOFTWARE IS PROVIDED BY BLUEKITCHEN GMBH AND CONTRIBUTORS
 * ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
 * LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
 * FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL MATTHIAS
 * RINGWALD OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
 * INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
 * LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY
 * WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
 * POSSIBILITY OF SUCH DAMAGE.
 *
 */

//==============================================================================================
/**
 * @file    btkeyLib.h
 * @brief   btkeyLib (Pro Controller エミュレータ) が公開する API
 *
 * ホストアプリケーション (btkeyLib.dll / btkeyLib.so をロードする側) から
 * 呼び出される関数の宣言。実装は btkeyLib.c、run loop の起動/停止は
 * port/{windows-winusb,libusb}/main.c の start_gamepad()/shutdown_gamepad()。
 */
//==============================================================================================

#ifndef BTKEYLIB_H
#define BTKEYLIB_H

#include <stdbool.h>
#include <stdint.h>

#ifdef _WIN32
#define BTKEYLIB_API __declspec(dllexport)
#else
#define BTKEYLIB_API __attribute__((visibility("default")))
#endif

#if defined __cplusplus
extern "C" {
#endif

/**
 * @brief Nintendo Switch / Switch 2 本体に接続済みかを返す
 * @note  HID 接続が確立し、かつハンドシェイク (サブコマンド 0x30 への応答) が
 *        完了した時点で true になる。切断されると false に戻る。
 */
bool BTKEYLIB_API gamepad_paired(void);

/**
 * @brief ボタン押下状態を設定する
 * @param button_status ボタンビットマスク (24bit)
 * @param press_time    未使用 (ABI 互換のため残置)
 */
void BTKEYLIB_API send_button(uint32_t button_status, uint32_t press_time);

/**
 * @brief 右スティックの状態を設定する
 * @param stick_horizontal 水平方向 (12bit)
 * @param stick_vertical   垂直方向 (12bit)
 * @param press_time       未使用 (ABI 互換のため残置)
 */
void BTKEYLIB_API send_stick_r(uint32_t stick_horizontal, uint32_t stick_vertical, uint32_t press_time);

/**
 * @brief 左スティックの状態を設定する
 * @param stick_horizontal 水平方向 (12bit)
 * @param stick_vertical   垂直方向 (12bit)
 * @param press_time       未使用 (ABI 互換のため残置)
 */
void BTKEYLIB_API send_stick_l(uint32_t stick_horizontal, uint32_t stick_vertical, uint32_t press_time);

/**
 * @brief ジャイロの状態を設定する
 */
void BTKEYLIB_API send_gyro(int16_t gyro1, int16_t gyro2, int16_t gyro3);

/**
 * @brief 加速度センサの状態を設定する
 */
void BTKEYLIB_API send_accel(int16_t accel_x, int16_t accel_y, int16_t accel_z);

/**
 * @brief コントローラのカラーを設定する
 * @note  各色は 0x00RRGGBB
 */
void BTKEYLIB_API send_padcolor(uint32_t pad_color, uint32_t button_color, uint32_t leftgrip_color,
                                uint32_t rightgrip_color);

/**
 * @brief amiibo ダンプファイル (NTAG215, 540 byte) を読み込む
 * @param path ダンプファイルのパス
 */
void BTKEYLIB_API send_amiibo(const char * path);

/**
 * @brief 直前に受信した振動データが「振動あり」かどうかを返す
 */
bool BTKEYLIB_API get_rumble(void);

/**
 * @brief 振動受信時に自動で押下扱いにするボタンのビットマスクを設定する
 */
void BTKEYLIB_API rumble_register(uint32_t key);

#if defined __cplusplus
}
#endif

#endif // BTKEYLIB_H
