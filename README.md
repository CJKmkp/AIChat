# AI 助手（AIChat）

> 独立悬浮 AI 聊天助手插件 —— 支持 OpenAI 兼容与 Anthropic Claude 原生协议，流式回答可一键插入画布。

| 项 | 值 |
| --- | --- |
| 插件 ID | `com.icc.ai-chat` |
| 作者 | ICC-CE |
| 版本 | 1.1.0 |
| 最低宿主版本 | 1.7.19 |
| 所需权限 | `Network` · `Canvas` · `Settings` |
| 协议 | 开源（LICENSE） |

**AI 助手**是面向课堂/板书场景的独立 AI 聊天插件：一个不抢焦点的悬浮按钮常驻在屏幕上，点开即可与 AI 对话；回答以流式实时呈现，支持「思考过程」折叠展示，并可直接作为可拖动文本元素插入画布，用于课堂生成板书、讲解词或补充材料。所有能力基于 `InkCanvas.PluginSdk` 宿主服务实现，不修改宿主主程序与 SDK。

---

## ✨ 功能特性

### 悬浮按钮
- **无焦点悬浮**：`WS_EX_NOACTIVATE` 置顶小窗，点击/显示不激活、不抢键盘焦点，不打断正在进行的板书/演示。
- **拖动吸附**：左侧 22px 拖动把手自由拖动，松手自动吸附最近屏幕边缘（默认贴主屏工作区右边缘垂直居中，避开宿主「快抽」按钮）。
- **右键菜单**：打开聊天 / 设置 / 隐藏悬浮按钮。
- **智能复位**：启动时自动贴回宿主主窗口右侧，多屏/DPI 切换后位置越界时自动夹紧回工作区，不会"消失"。

### 聊天窗口
- **Qwen 风格布局**：顶部模型标签 + 分组模型下拉，中部消息列表，底部胶囊输入条。
- **流式输出**：SSE 增量实时渲染，边生成边显示。
- **思考过程**：DeepSeek `reasoning_content` / Claude `thinking_delta` 单独累积，在「思考过程」折叠区展示，不混进正文；生成前显示「思考中」三点动画。
- **模型快速切换**：下拉按「提供商 → 模型」分组，点击即切换 provider 并换模型。
- **输入交互**：`Enter` 发送 / `Shift+Enter` 换行；支持停止生成、清空对话（带确认）、关闭。
- **气泡操作**：每条回答可复制 / 插入画布 / 重新生成。
- **一键插入画布**：顶部按钮或气泡按钮，把 AI 回答作为可拖动文本元素插入画布——固定宽度、高度上限内滚动，任意长度不截断。
- **会话历史**：自动落盘，重开聊天窗恢复；跟随宿主置顶状态（`TopmostChanged`）。

### 多协议、多提供商
| 协议 | 服务商 |
| --- | --- |
| OpenAI 兼容 | DeepSeek · OpenAI · 智谱 GLM · Moonshot Kimi · 通义千问 DashScope 兼容模式 · 豆包（火山方舟）· Ollama（本地）· 任意自定义中转站 |
| Anthropic 原生 | Claude（`x-api-key` + `anthropic-version` 头、system 单独字段、`content_block_delta.text_delta`） |

- 内置常用服务商模板，可**任意添加 / 删除 / 切换**，每个 provider 独立保存 API Key 与模型。
- `BaseUrl` 自动规范化：纯域名自动补 `/v1`，已含路径原样保留；兼容中转站返回**非流式 JSON** 的情况。

### 设置页（master-detail 双栏）
- 左列：提供商列表，模板一键添加 / 删除（至少保留一个）。
- 右列：名称 · 协议 · 接口地址 · API Key（显示/隐藏切换）· 模型（手动填写或从列表选择）。
- 底部全局：**系统提示词**（默认教学助手）· **温度** · **Max Tokens**（0 = 不发送，使用服务端默认值）。
- 便捷工具：**测试连接**（发一条最短消息验证）、**一键拉取模型列表**（`GET /models` 或 `/v1/models`）、**端点测速**（延迟 + HTTP 状态）。

---

## 🚀 快速开始

1. **安装插件**：将编译产物放入宿主 `Plugins` 目录（或从插件市场/Release 安装），插件 ID `com.icc.ai-chat`。
2. **打开设置**：右键悬浮按钮 → **设置**，或从宿主设置页进入插件配置。
3. **选择服务商**：左列「＋ 添加」选择模板（如 DeepSeek / Claude / Ollama 本地），或直接编辑默认的 DeepSeek。
4. **填写并保存**：填写 API Key、模型名（可点「拉取模型」自动获取），可选调整系统提示词 / 温度 / Max Tokens，建议先点「测试连接」验证。
5. **开始对话**：点悬浮按钮打开聊天窗，输入问题回车发送；回答生成后点 **插入画布** 即可上屏。

> 💡 **Ollama 本地使用**：地址填 `http://localhost:11434/v1`，无需 API Key（随意填非空即可），模型如 `qwen2.5:7b`。

---

## 🔒 数据存储与安全

| 文件 | 内容 |
| --- | --- |
| `config.json` | 提供商列表、当前 provider、系统提示词、温度、Max Tokens、悬浮按钮位置 |
| `history.json` | 会话历史（含全部气泡） |

- **API Key 不落明文**：使用 Windows **DPAPI**（`CryptProtectData` + 应用级熵加盐）加密后以密文存储，仅当前 Windows 用户可解密。
- 旧版（单 provider 顶层字段）配置**自动迁移**到新结构，不丢失已有 Key 与地址。

---

## 🔧 开发与构建

- **目标框架**：`net6.0-windows10.0.19041.0`（WPF）
- **UI 库**：`iNKORE.UI.WPF.Modern 0.10.2.1`（与宿主一致，主题自动适配）
- **SDK**：`InkCanvas.PluginSdk 1.7.19.9-gda522a8757`（NuGet）
- **测试**：`AIChat.Tests`（xUnit）——配置读写/迁移/DPAPI、端点测速、SSE 解析、OpenAI/Anthropic delta 提取。
- **CI**：GitHub Actions —— `build.yml`（push/PR 构建 + 自动发布）、`release.yml`（打 `v*` tag 发布）。

```bash
dotnet build AIChat.csproj -c Release
dotnet test AIChat.Tests/AIChat.Tests.csproj
```

### 项目结构

```
AIChat/
├── AIChatPlugin.cs            # 插件入口：服务解析、窗口管理、画布插入
├── ConfigStore.cs             # 配置/多 provider 管理、旧配置迁移、DPAPI Key 加解密
├── SecretStore.cs             # DPAPI 封装（CryptProtectData/CryptUnprotectData）
├── Models.cs                  # 消息/会话/悬浮位置模型
├── Strings.cs                 # 中英双语字符串表（zh → en → 键名回退）
├── ChatService/
│   ├── IChatClient.cs         # 客户端抽象 + SSE 解析器 + 共享 HttpClient
│   ├── OpenAiChatClient.cs    # OpenAI 兼容流式客户端
│   ├── AnthropicChatClient.cs # Claude 原生流式客户端
│   └── EndpointSpeedTest.cs   # 端点延迟测速
├── Views/                     # 悬浮按钮 / 聊天窗 / 气泡 / 设置页
└── AIChat.Tests/              # xUnit 单元测试
```

---

## 📄 许可

本插件基于仓库 [LICENSE](LICENSE) 许可开源。仅供学习与教学场景使用，请遵守所用 AI 服务商的服务条款。
