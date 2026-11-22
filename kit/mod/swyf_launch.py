# Launcher used by the game (SwyfCompat.VieNeuTts) to run the VieNeu OpenAI-compatible speech server.
# Usage: pythonw swyf_launch.py <log file> <game pid> <port>
#  - localhost only, CPU int8 (override with VIENEU_PRECISION in the environment)
#  - one stream at a time (one caller speaks at a time; keeps memory use down)
#  - stdout/stderr go to the log file (pythonw has no console)
#  - exits by itself when the game process ends, even after a crash
#  - mod installer layout: <game>\SwyfMod\VieNeu-TTS (this file) + <game>\SwyfMod\hf-cache (models)
import ctypes
import os
import runpy
import sys
import threading

log_path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.environ.get("TEMP", "."), "swyf-vieneu-server.log")
parent_pid = int(sys.argv[2]) if len(sys.argv) > 2 else 0
port = sys.argv[3] if len(sys.argv) > 3 else "8000"

_hf_cache = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir, "hf-cache"))
if os.path.isdir(_hf_cache):
    os.environ.setdefault("HF_HOME", _hf_cache)
os.environ.setdefault("VIENEU_PRECISION", "int8")
os.environ.setdefault("VIENEU_MAX_STREAMS", "1")
os.environ["HOST"] = "127.0.0.1"
os.environ["PORT"] = port
os.environ["PYTHONIOENCODING"] = "utf-8"

log = open(log_path, "w", encoding="utf-8", buffering=1)
sys.stdout = log
sys.stderr = log


def _exit_with_parent() -> None:
    SYNCHRONIZE = 0x00100000
    kernel32 = ctypes.windll.kernel32
    handle = kernel32.OpenProcess(SYNCHRONIZE, False, parent_pid)
    if handle:
        kernel32.WaitForSingleObject(handle, 0xFFFFFFFF)
    log.write("[swyf_launch] game process ended; stopping VieNeu server\n")
    log.flush()
    os._exit(0)


if parent_pid > 0:
    threading.Thread(target=_exit_with_parent, daemon=True).start()

sys.argv = ["apps.openai_speech"]
runpy.run_module("apps.openai_speech", run_name="__main__", alter_sys=True)
