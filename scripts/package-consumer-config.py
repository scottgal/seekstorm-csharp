#!/usr/bin/env python3
"""Keep local feed paths and HTTPS feed URLs distinct on every CLI shell."""
from pathlib import Path
from xml.etree import ElementTree as ET
root = Path(__file__).resolve().parents[1]
configuration = ET.Element("configuration")
sources = ET.SubElement(configuration, "packageSources")
ET.SubElement(sources, "clear")
ET.SubElement(sources, "add", key="candidate", value=str(root / "nupkgs"))
ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
ET.ElementTree(configuration).write(root / ".nuget-package-consumer.config", encoding="utf-8", xml_declaration=True)
