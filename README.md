A Juno: New Origins mod for adding raymarched volumetric clouds to planets.

## 协作规则 / Collaboration Rules

- **构建/打包/部署由用户负责**:mod.build、Unity 菜单"一键打包并部署"、生成/部署 .sr2-mod 产物等,均由用户手动处理。agent 无需关心构建过程、构建产物或部署步骤(不自动跑 Unity 打包)。
  **Build/packaging/deploy is the user's job**: mod.build, the Unity "一键打包并部署" menu action, and producing/deploying the .sr2-mod artifact are handled manually by the user. The agent does not need to care about the build process, artifacts, or deployment steps (no auto-running Unity packaging).

- agent 只负责源码、shader、配置、文档等仓库内改动,并(在可行时)做编译级验证(如 `dotnet build`)。
  The agent is responsible only for in-repo changes (source, shaders, config, docs) and, when feasible, compile-level validation (e.g. `dotnet build`).
