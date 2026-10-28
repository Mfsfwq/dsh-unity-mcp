# DSH Unity Bridge

让 DSH（以及任何会发 HTTP 的东西）直接驱动 Unity 编辑器：读场景、建对象、改组件、截图、存场景。

**零第三方依赖**：Unity 侧只用 BCL（`TcpListener` + 手写最小 HTTP + 自带 JSON），DSH 侧只用 Node 内置 `http`。
不依赖 Newtonsoft / EditorCoroutines，**不依赖 Unity 6**，也不需要 `com.unity.pipeline` —— 2022.3 就能跑。

## 为什么不用 mcp-unity / 官方 Unity CLI

| 方案 | 问题 |
|---|---|
| `com.gamelovers.mcp-unity` | 依赖 Newtonsoft + EditorCoroutines，且要靠 MCP 客户端接 stdio 子进程；本项目已因兼容问题移除 |
| 官方 `unity` CLI + `com.unity.pipeline` | 需要 Unity 6；国内渠道拿不到 Unity 6（团结引擎内核仍是 2022），且 `unity.exe` 每次启动都写 `%APPDATA%\UnityHub`，在受限沙箱里 EPERM |
| **本插件** | Editor 包 + 一行 HTTP，编辑器一打开就自启，DSH 侧零配置 |

## 组成

整个插件就是一个文件夹，可以整体拷走：

```
dsh-unity-mcp/
├── README.md              # 本文件
├── bridge.mjs             # DSH 侧 CLI（Node，零依赖）
├── compile-check.ps1      # 不开 Unity 也能查语法/API 错误
└── unity-package/         # Unity 侧（UPM 包 com.dsh.unity-bridge）
    ├── package.json
    └── Editor/
        ├── DshBridge.Editor.asmdef
        ├── DshBridgeCore.cs     # TcpListener + 最小 HTTP + 主线程泵 + token 鉴权
        ├── DshBridgeJson.cs     # 最小 JSON（parse / stringify）
        ├── DshBridgeOps.cs      # 全部操作实现
        └── DshBridgeWindow.cs   # Tools → DSH Unity Bridge 窗口
```

## 装进一个 Unity 工程

包不在 `UnityProject/Packages/` 里，而是由 UPM 的 `file:` 依赖指向本文件夹，所以源码只有一份：

```json
// UnityProject/Packages/manifest.json
{
  "dependencies": {
    "com.dsh.unity-bridge": "file:../../dsh-unity-mcp/unity-package",
    ...
  }
}
```

- 路径相对 `Packages/` 解析：`../../dsh-unity-mcp/unity-package` = 仓库根的 `dsh-unity-mcp/unity-package`。
- 换工程/换机器改这一行即可；`packages-lock.json` 由 Unity 自动维护（`source: local`）。
- `file:` 包是可改的本地包，改完 `Editor/*.cs` 回到 Unity 等重编译即可，不需要打包 / 发布。

### 用脚本装（推荐）

现成工程里手改容易漏，直接跑：

```powershell
# 装进目标工程
powershell -NoProfile -ExecutionPolicy Bypass -File dsh-unity-mcp\install.ps1 -ProjectPath "E:\path\to\UnityProject"

# 撤销
powershell -NoProfile -ExecutionPolicy Bypass -File dsh-unity-mcp\install.ps1 -ProjectPath "E:\path\to\UnityProject" -Undo
```

脚本行为：

- 只改目标工程 `Packages/manifest.json` 里的 `com.dsh.unity-bridge` 一行，其它依赖原样保留；
- 同盘符算相对路径（如 `file:../../../dsh-unity-mcp/unity-package`），**跨盘符自动退回绝对路径**（`file:E:/dsh/...`），Unity 两者都认；
- 已经是目标值就跳过（幂等）；
- 改前备份 `manifest.json.bak`，落盘前用 `ConvertFrom-Json` 校验，校验不过不写。

## 启动

1. Unity 打开 `UnityProject/`，等编译完。
2. 菜单 **Tools → DSH Unity Bridge** → 点「启动」（默认端口 8090，`AutoStart` 默认开，之后随编辑器自启）。
3. 首次启动会在 `UnityProject/UserSettings/UnityBridge.json` 生成 token —— **这个文件已在 `.gitignore` 里，不进版本库**。

> `UserSettings/` 被忽略，所以换机器/清缓存后 token 会重新生成，DSH 侧自动从该文件读取，无需手工同步。

## DSH 侧调用

```powershell
node dsh-unity-mcp/bridge.mjs ping
node dsh-unity-mcp/bridge.mjs scene
node dsh-unity-mcp/bridge.mjs hierarchy --depth 4
node dsh-unity-mcp/bridge.mjs find --query Player
node dsh-unity-mcp/bridge.mjs create --name Torch --sprite Assets/Art/Sprites/Props/torch.png --position 3,1,0
node dsh-unity-mcp/bridge.mjs set --path Torch --component SpriteRenderer --field sortingOrder --value 5
node dsh-unity-mcp/bridge.mjs transform --path Player --position -24.8,-11.1,0
node dsh-unity-mcp/bridge.mjs save
node dsh-unity-mcp/bridge.mjs screenshot --view scene
```

连接参数：`--project <工程根>` / `--url <rpc>` / `--token <...>`，也可用 `DSH_UNITY_PROJECT` / `DSH_UNITY_URL` / `DSH_UNITY_TOKEN`。
不传时从 cwd 向上自动发现（会认仓库根下的 `UnityProject/`）。

## 协议

`POST http://127.0.0.1:8090/rpc`，头 `Authorization: Bearer <token>`（也接受 `?token=`）：

```json
{ "op": "hierarchy", "params": { "depth": 2 } }
```

成功：`{ "ok": true, "op": "...", "data": ... }`；失败：HTTP 500 + `{ "ok": false, "error": "..." }`。
`GET /health` 免鉴权，只回包名/端口/Unity 版本，用来探活。

## op 一览

| op | 参数 | 说明 |
|---|---|---|
| `ping` | – | 包名、版本、端口、运行状态、Unity 版本、工程路径、是否 Play |
| `scene` | – | 当前场景路径、脏标记、根对象数、对象总数 |
| `open_scene` | `path` `additive`(false) | 打开场景（批处理模式也能用） |
| `assets` | `filter` `folder`(Assets) `type` `limit`(100) | 搜资源（`filter` 用 Unity 的 `t:` 语法） |
| `hierarchy` | `depth`(4) `budget`(600) `components`(true) | 场景树 + 每个对象的关键组件信息 |
| `find` | `query` 或 `type` | 按名字/路径/组件类型找对象（最多 200 条） |
| `create` | `name` `parent` `position` `localPosition` `scale` `layer` `tag` `sprite` `order` `sortingLayer` | 新建空对象或精灵对象 |
| `delete` | `path` | 删对象（可 Undo） |
| `rename` | `path` `to` | 改名 |
| `set_active` | `path` `active` | 激活/停用 |
| `transform` | `path` `position` `localPosition` `scale` `eulerAngles` | 改 Transform |
| `add_component` | `path` `type` | 加组件（类型名或全名） |
| `remove_component` | `path` `type` | 删组件 |
| `set` | `path` `component` `field` `value` | 改组件字段，先走 SerializedProperty，再回退反射 |
| `save` | `all`(false) | 保存当前场景（或全部打开的场景） |
| `screenshot` | `view`(scene/game) `width` `height` `path` | 相机渲染成 PNG，默认存到工程同级 `screenshots/` |
| `play` | `action`(start/stop/pause/resume) | 播放控制 |
| `menu` | `path` | 执行菜单项，例如 `Tools/MCP Unity/Server Window` |
| `select` | `path` | 选中并 ping 对象 |
| `invoke` | `type` `method` `args[]` | 反射调静态方法（逃生口） |
| `log` | `lines`(40) | 读 `Editor.log` 尾部 |
| `quit` | `code`(0) | 退出编辑器（先回包再退，延迟 0.5s） |

`path` 写法：`Player`（全局唯一名）、`Root/Child`（层级路径）或 `#instanceId`。

## 安全边界

- 只监听 `127.0.0.1`（回环），不对外开放。
- 所有 `/rpc` 请求要 token；token 在 `UserSettings/UnityBridge.json`，不进 git。
- `screenshot` 默认写到工程外（`<工程>/../screenshots/`），避免污染 `Assets/`。

## 排障

| 现象 | 处理 |
|---|---|
| `连接失败: connect ECONNREFUSED` | 编辑器没开，或窗口里没点「启动」；看 Console 里 `[DshBridge]` 日志 |
| `token 无效` | 重新读 `UserSettings/UnityBridge.json`；窗口里点「复制 token」核对 |
| `端口 8090 被占用` | 启动时自动 +1；或窗口里改端口后用 `--port`/`--url` 指定 |
| 请求超时 | 编辑器在编译/导入资源，主线程泵停摆；等编译完再试 |
| 改完没生效 | 忘记 `save`；或改的是不该在运行时改的字段 |

## 编译校验（不开编辑器也能查语法/API）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File dsh-unity-mcp\compile-check.ps1
```

脚本自己建临时 netstandard2.1 工程、引用 `D:\Unity\2022.3.54f1c1\Editor\Data\Managed` 下的
`UnityEngine*.dll` / `UnityEditor*.dll`，编完即删；换编辑器版本用 `-UnityEditor <路径>` 覆盖。
当前 4 个 `.cs` 在该校验下 **0 警告 0 错误**。

> 脚本里含中文，**必须存成带 BOM 的 UTF-8**——PowerShell 5.1 会把无 BOM 的 UTF-8 当 ANSI 解析并报
> `The string is missing the terminator: "`。

## 批处理模式（CI / 无界面）注意事项

```powershell
Unity.exe -batchmode -projectPath E:\dsh\test\UnityProject -logFile <日志>
```

- **启动时是空场景**，不是上次打开的场景：先 `open-scene --path Assets/Scenes/TestRoom.unity` 再干活，
  否则 `hierarchy` 返回空、`screenshot` 报「找不到可用相机」。
- 自启靠 `EditorApplication.update` 兜底，绕开了 `-quit` 前 `delayCall` 不执行的坑；实测 **9~12 秒**端口可用。
- `quit` 先回包再延迟退出，**进程真正结束还要几十秒**（许可证客户端断开、AssetDatabase 收尾），
  脚本里别用 8 秒超时硬等，给 40~60 秒。
- Windows 上 `Unity.exe` 启动要写 `%APPDATA%\Unity`、`%APPDATA%\UnityHub`：若进程被沙箱限制会报
  `EPERM: operation not permitted, mkdir ...UnityHub\languageConfig.json.lock`，需要放开文件访问。

## 导出成独立仓库

`dsh-unity-mcp/` 本身不含任何游戏工程内容，整个文件夹拷走就能用。想在别的仓库里单独维护，两种方式：

**A. 直接拆出目录（最简单）**

```powershell
# 在一个空目录里初始化独立仓库
Copy-Item -Recurse E:\dsh\test\dsh-unity-mcp E:\dsh\unity-bridge
cd E:\dsh\unity-bridge
git init -b main
git add .
git commit -m "chore: 初始版本 v0.1.0"
```

其它工程就 `install.ps1 -PackagePath "E:\dsh\unity-bridge\unity-package"`。

**B. 保留历史（从本仓库抽子树）**

```powershell
git subtree split -P dsh-unity-mcp -b plugin-standalone
git push <新仓库地址> plugin-standalone:main
```

抽出来的分支只含 `dsh-unity-mcp/` 下的内容；之后本仓库继续改，再 `git subtree push` 同步。

> 独立仓库里记得一起带上 `unity-package/Editor/*.meta` —— Unity 靠 `.meta` 的 guid 认资源，
> 漏了会导致首次导入时 guid 重排。本仓库的 `.meta` 已在版本控制里。
