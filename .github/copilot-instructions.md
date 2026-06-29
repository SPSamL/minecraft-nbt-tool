- [x] Verify that the copilot-instructions.md file in the .github directory is created.
- [x] Clarify Project Requirements
- [x] Scaffold the Project
- [x] Customize the Project
- [x] Install Required Extensions
- [x] Compile the Project
- [ ] Create and Run Task
- [ ] Launch the Project
- [x] Ensure Documentation is Complete

Project notes:
- The app is a .NET 8 C# console tool.
- It scans Minecraft NBT-style files and emits grouped JSON reports.
- Supported extensions currently include `.nbt`, `.blueprint`, `.dat`, `.schem`, `.schematic`, and `.snbt`.
- Use `dotnet run -- scan <root> --output report.json` for a full scan.
