#!/usr/bin/env python3
"""Static XAML checks that the compiler does not perform.

1. Every {StaticResource Key} resolves to a key defined in the theme dictionaries,
   App.xaml or the same file, and theme keys are defined before they are used.
2. Every simple {Binding Path} names a public property that exists somewhere in the
   C# sources (a typo check, not a type check).

Exits non-zero when a problem is found. Run from the repository root.
"""
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parent.parent
app = root / "src" / "Phantom.App"
key_re = re.compile(r'x:Key="([^"]+)"')
use_re = re.compile(r'\{StaticResource ([^}\s]+)\}')
binding_re = re.compile(r'\{Binding(?:\s+Path=)?\s*([A-Za-z_][\w.]*)?[^}]*\}')
problems = []

# --- resource keys ---
global_keys = set()
for name in ("Themes/Colors.xaml", "Themes/Controls.xaml", "App.xaml"):
    text = (app / name).read_text(encoding="utf-8")
    seen = set(global_keys)
    for line_no, line in enumerate(text.splitlines(), 1):
        for key in key_re.findall(line):
            seen.add(key)
        for used in use_re.findall(line):
            if used.startswith("{x:Type"):
                continue
            if used not in seen:
                problems.append(f"{name}:{line_no}: StaticResource '{used}' is used before it is defined")
    global_keys = seen

for path in sorted(app.rglob("*.xaml")):
    if "obj" in path.parts or "bin" in path.parts or path.parent.name == "Themes" or path.name == "App.xaml":
        continue
    text = path.read_text(encoding="utf-8")
    local = set(key_re.findall(text))
    for line_no, line in enumerate(text.splitlines(), 1):
        for used in use_re.findall(line):
            if used not in global_keys and used not in local:
                problems.append(f"{path.relative_to(root)}:{line_no}: unknown StaticResource '{used}'")

# --- binding paths ---
props = set()
prop_re = re.compile(r'public\s+(?:static\s+|override\s+|required\s+|virtual\s+)*[\w<>\[\]?,. ]+?\s+(\w+)\s*(?:\{|=>|;|=)')
record_re = re.compile(r'record\s+(?:struct\s+)?\w+(?:<\w+>)?\(([^)]*)\)')
class_ctor_re = re.compile(r'class\s+\w+\(([^)]*)\)')
for path in (root / "src").rglob("*.cs"):
    if "obj" in path.parts or "bin" in path.parts:
        continue
    text = path.read_text(encoding="utf-8")
    props.update(prop_re.findall(text))
    for params in record_re.findall(text):
        for param in params.split(","):
            parts = param.strip().split()
            if parts:
                props.add(parts[-1])
framework = {"Count", "Length", "DataContext", "Tag", "IsDropDownOpen", "ActualWidth", "Background", "BorderBrush"}
for path in sorted(app.rglob("*.xaml")):
    if "obj" in path.parts or "bin" in path.parts:
        continue
    for line_no, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        for match in binding_re.finditer(line):
            binding_path = match.group(1)
            if not binding_path or "RelativeSource" in match.group(0) and binding_path in framework:
                continue
            for segment in binding_path.split("."):
                if segment not in props and segment not in framework:
                    problems.append(f"{path.relative_to(root)}:{line_no}: binding segment '{segment}' in '{binding_path}' matches no public property")

if problems:
    print("\n".join(problems))
    sys.exit(1)
print(f"XAML checks passed ({len(global_keys)} theme resources, {len(props)} known properties).")
