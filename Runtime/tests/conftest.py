import os
import sys

# Bridges live in Runtime/ as standalone scripts; make them and the fake /ws importable from tests.
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.dirname(HERE))
