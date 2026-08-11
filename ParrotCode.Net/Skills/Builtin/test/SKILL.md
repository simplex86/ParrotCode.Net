---
name: test
description: 为指定代码生成单元测试。当用户要求写测试/生成测试时调用。
tools_allow:
  - read_file
  - write_file
  - grep
  - glob
  - run_command
tools_deny:
  - skill_loader
---

# Test Generation SOP

生成单元测试时遵循以下步骤：

1. 用 `read_file` 阅读待测试的源码文件，理解其公共 API 和行为
2. 用 `grep` / `glob` 查找同项目已有的测试文件，确认测试框架与风格约定
3. 为每个公共方法设计测试用例，覆盖：
   - 正常路径（happy path）
   - 边界条件（空、null、极值）
   - 异常路径（错误输入、异常）
4. 用 `write_file` 创建测试文件，命名遵循 `{Class}Tests.cs` 约定
5. 调用 `run_command` 执行 `dotnet test` 验证测试通过
6. 如测试失败，分析原因并修正测试或反馈源码问题

注意：
- 测试方法名清晰表达意图（如 `Add_WhenNumbersPositive_ReturnsSum`）
- 遵循 AAA 模式（Arrange-Act-Assert）
- 一个测试只验证一个行为
