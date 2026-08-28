# Controller personalities

This directory will contain controller personalities loaded into bridge RAM at runtime. The current Switch Pro-compatible behavior is compiled into the proof firmware, so no runtime personality ships yet.

Do not add a personality until its descriptors, report layout, polling limits, and handler requirements pass the validation rules in [the architecture](../docs/architecture.md#controller-personality-format).
