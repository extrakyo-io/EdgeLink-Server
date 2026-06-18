# 迷你 Modbus TCP Slave 模擬器
# 用法:
#   pip install pymodbus==3.6.6
#   python modbus_slave_sim.py
# 然後 EdgeLink 那邊 Slave IP 填 127.0.0.1, Modbus Port = 5020
# (Windows 沒裝服務模式下 502 通常被佔，所以用 5020)

import threading
import time
import math
from pymodbus.server import StartTcpServer
from pymodbus.datastore import ModbusSequentialDataBlock, ModbusSlaveContext, ModbusServerContext

HOST = "0.0.0.0"
PORT = 5020   # Modbus 預設 502 — Windows 上若被占改用 5020

# 16 個 DI (給 Yaw 8bit + Pitch 8bit GrayCode 編碼器測試用)
# 32 個 Holding Register (給 ET-7017 類比輸入測試用)
di_block = ModbusSequentialDataBlock(0, [0] * 16)
hr_block = ModbusSequentialDataBlock(0, [0] * 32)
co_block = ModbusSequentialDataBlock(0, [0] * 16)
ir_block = ModbusSequentialDataBlock(0, [0] * 32)

slave = ModbusSlaveContext(di=di_block, co=co_block, hr=hr_block, ir=ir_block)
context = ModbusServerContext(slaves=slave, single=True)


def gray_encode(n: int) -> int:
    """二進位 → GrayCode (8-bit)"""
    return n ^ (n >> 1)


def updater():
    """每 50ms 變動一次資料，模擬編碼器旋轉 + 類比訊號震盪"""
    t = 0
    while True:
        # Yaw 軸：0~255 線性掃描 → GrayCode → 8 個 DI bits (addr 0~7)
        yaw_raw = t % 256
        yaw_gray = gray_encode(yaw_raw)
        # Pitch 軸：30Hz sine wave 0~255 → GrayCode → 8 個 DI bits (addr 8~15)
        pitch_raw = int(127 + 127 * math.sin(t * 0.05))
        pitch_gray = gray_encode(pitch_raw)

        bits = [0] * 16
        for i in range(8):
            bits[i] = (yaw_gray >> i) & 1
            bits[8 + i] = (pitch_gray >> i) & 1
        di_block.setValues(0, bits)

        # Holding Register: 32 個值循環
        hr = [(t + i * 7) % 65536 for i in range(32)]
        hr_block.setValues(0, hr)

        # Input Register: 類比 1.0~5.0V 模擬搖桿 X/Y (FluentModbus 讀 float32 用)
        # X = 中心 2500 + 浮動  Y = 中心 2500 + 浮動
        ir = [
            int(2500 + 1500 * math.sin(t * 0.03)),  # X
            int(2500 + 1500 * math.cos(t * 0.03)),  # Y
        ] + [0] * 30
        ir_block.setValues(0, ir)

        t += 1
        time.sleep(0.05)


if __name__ == "__main__":
    threading.Thread(target=updater, daemon=True).start()
    print(f"[Modbus Slave Sim] Listening on {HOST}:{PORT}")
    print(f"  DI  0~7:  Yaw   軸 GrayCode 8-bit (持續遞增)")
    print(f"  DI  8~15: Pitch 軸 GrayCode 8-bit (sine wave)")
    print(f"  HR  0~31: 循環遞增")
    print(f"  IR  0,1:  搖桿 X/Y sine/cos")
    print(f"\nEdgeLink 設定: Slave IP=127.0.0.1, Modbus Port={PORT}, Slave ID=1")
    StartTcpServer(context=context, address=(HOST, PORT))
