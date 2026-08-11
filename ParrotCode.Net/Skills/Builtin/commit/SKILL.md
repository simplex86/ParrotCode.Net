---
name: commit
description: 按 Conventional Commits 规范提交代码。当用户要求提交/commit 时调用。
tools_allow:
  - read_file
  - write_file
  - run_command
  - grep
  - glob
tools_deny:
  - skill_loader
---

# Commit SOP

执行 git 提交时遵循以下步骤：

1. 调用 `run_command` 执行 `git status` 查看当前变更
2. 用 `read_file` 查看修改过的关键文件，理解变更意图
3. 根据变更内容，按 Conventional Commits 规范生成 commit message：
   - `feat`: 新功能
   - `fix`: 修复 bug
   - `docs`: 文档变更
   - `refactor`: 重构（不影响功能）
   - `test`: 测试相关
   - `chore`: 构建/工具/依赖
4. 执行 `git add -A` 暂存所有变更
5. 执行 `git commit -m "<生成的 message>"`
6. **不要执行 `git push`**，除非用户明确要求推送

注意：
- commit message 用简洁中文或英文，首行不超过 72 字符
- 如有多个不相关变更，建议分多次提交
- 提交完成后向用户报告提交结果（commit hash、变更统计）
