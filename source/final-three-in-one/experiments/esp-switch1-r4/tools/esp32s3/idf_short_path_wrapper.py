"""Run ESP-IDF without expanding a Windows subst drive back to its long path."""

import os
import runpy
import sys


def _absolute_path(path: str, *, strict: bool = False) -> str:
    del strict
    return os.path.abspath(path)


os.path.realpath = _absolute_path

entrypoint = os.environ["Y700_IDF_ENTRYPOINT"]
sys.path.insert(0, os.path.dirname(entrypoint))
sys.argv[0] = entrypoint
runpy.run_path(entrypoint, run_name="__main__")
