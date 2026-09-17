# 制作 JSON 更新包

更新仍通过启动器选择压缩包。支持 `.7z`、`.zip`、`.rar`、`.001`，文件名必须以 `UPDATE_LAZY_KFC` 开头。首次使用前，请先部署本版本的外层 Launcher、主程序和 MediaUpdater。旧 `sync.bat` 更新包不再接受，也不会执行包内脚本。

## 包目录与校验

```text
UPDATE_LAZY_KFC_20260908.zip
├── update
├── checksums
└── source/
    ├── contents/
    └── launcher/
```

整个解压目录必须只有一份 `update`，可以有外层包装目录，但所有文件都必须位于 `update` 所在目录内；不要在外层放置额外说明文件。源路径相对于清单所在目录且必须位于 `source/`；目标路径相对于游戏根目录。仅删除或编辑 XML 的包可以没有 `source/`，但仍必须有 `checksums`。

参考 [清单模板](update) 与 [JSON Schema](update.schema.json)。模板中的所有源目录必须实际存在；不发布某部分时删除对应操作。Schema 提供编辑器校验；程序检查基础路径约束，并在预演中检查实际文件、操作冲突与访问权限。

旧包需将 `update.json` 重命名为 `update`、移除清单中的 `schemaVersion`、删除旧 `checksums.json`，再运行生成工具生成 `checksums` 并重新打包。旧文件名和版本字段不再作为清单格式接受；编辑器使用的 `.schema.json` 文件名保持不变。

`update` 和 `checksums` 均不带扩展名，内容仍为 UTF-8 严格 JSON，不支持注释、尾逗号、重复字段、未知字段或 null。两份清单均不包含 `schemaVersion`；`update` 的 `operations` 至少一项。同一个包或修改过内容的包均可安装，每次都会重新校验，并根据游戏目录的当前状态预演和安装。修改包内容后必须重新生成 `checksums`。

重复安装也会重新执行 XML 编辑：插入操作可能再次插入，新增已存在的属性或删除已不存在的 XML 节点会校验失败。需要支持反复执行的配置调整时，优先使用修改已有值的 `setValue`，并确保每次操作的 XPath 都满足唯一匹配要求。预演失败不安装；安装失败或中断会保留已经完成的修改，不回滚。

## 生成 SHA-256 校验清单

准备好 `update`、全部载荷和说明文件后，在仓库根目录运行（需要 Python 3.10 或更新版本，无第三方依赖）：

```text
python Tools/generate_update_checksums.py "F:\Share\UPDATE_LAZY_KFC_example"
```

工具自动定位唯一的 `update`，在其同级写入 `checksums`。生成后将整个包目录打成压缩包，无需手动填写摘要。修改任何文件后都必须重新生成，再重新打包；已有清单只有在计算全部成功后才被替换。重复生成相同内容会得到相同清单。

校验清单采用 UTF-8 严格 JSON，格式由 [checksums.schema.json](checksums.schema.json) 定义。以下仅为格式示意，实际摘要由工具生成：

```json
{
  "algorithm": "SHA256",
  "files": [
    {
      "path": "update",
      "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    }
  ]
}
```

- 所有文件都参与校验，包括 `update`、说明文件、隐藏文件和未被操作引用的载荷；仅排除包根目录的 `checksums` 自身。子目录中的同名文件仍参与校验。空目录不计算摘要。
- 路径相对于包根目录，以 `/` 分隔；SHA-256 为 64 位小写十六进制。拒绝重复字段、未知字段、null、重复路径、大小写冲突和非法路径。
- 解压后的文件集合必须和清单完全一致。缺少文件、额外文件、摘要不符或缺少清单都会停止更新，提示“更新包疑似被修改或损坏”及具体原因。
- 启动器解压后收集一次文件列表，核对清单中的全部文件 SHA-256；不计算额外整包摘要，不生成校验交接记录。
- 更新器只等待发起更新的启动器退出（最多 10 秒），然后按游戏目录当前状态预演一次文件操作和 XML 修改。全部目标通过占用、写入、删除及父目录创建权限检查后才安装；不扫描或终止其他进程。
- 校验与预演是两个阶段。更新器不会再次读取校验清单，也不核对载荷摘要或检测校验后的包变化；完成校验后不要再修改解压内容。
- 更新包没有版本去重；同一个包可以反复安装，每次都按当前目标状态执行。
- SHA-256 只发现文件与清单不一致，不验证发布来源。本版本不使用数字签名。

## 四类文件操作

| type | 必填字段 | 语义 |
|---|---|---|
| `copy` | `source`、`target` | 文件新增/覆盖，目录内容递归合并到目标目录；保留目标独有文件。 |
| `delete` | `target` | 删除文件或目录，不存在即成功。 |
| `mirror` | `source`、`target` | 源必须为目录，使目标目录内容与源一致，移除目标多余内容；空源可清空目标。 |
| `editXml` | `target`、`edits` | 通过 XPath 顺序修改已有 XML。可选 `encoding`、`namespaces`。 |

操作按清单顺序预演，后续读取前序结果，全部通过才应用最终结果。`copy` 遇到文件/目录冲突会失败，需先显式 `delete`；`mirror` 可以转换类型，但不能删除运行中的更新器。缓存清理、启动器镜像、omnimix 镜像都必须显式声明。

`config.toml` 与普通文件相同，可被复制新增、覆盖、镜像清理或删除。更新包不提供 TOML 内容编辑能力；旧 `editText` 操作仍会拒绝。

当前配置位置是外层 Launcher 旁边的 `config.toml`，正常部署时更新目标写为 `config.toml`，不要继续发布到 `launcher/config.toml`。Launcher 启动时会将旧位置文件复制覆盖到新位置并删除旧文件；配置缺失时创建默认值，损坏时备份修复，失败时停止启动。Launcher 默认以普通权限运行，仅新建配置被拒绝访问时提示申请管理员权限重试；其他准备失败不触发提权。主程序只读取已有配置并保存用户修改，不执行初始化，其管理员启动要求保持不变。

本版本外层 Launcher、主程序和 MediaUpdater 必须成套部署。成功安装后仅从游戏根目录启动 `启动.exe` 或 `Launcher.exe`；入口缺失时提示手动启动，不直接运行子目录中的主程序。安装阶段对配置的删除仍会生效；随后 Launcher 的初始化可能创建新的默认配置，这属于启动行为。


运行中的 `launcher/MediaUpdater.exe` 不直接替换，也不被父目录删除或镜像清理移除。复制此文件时最后写入 `.pending`，下次启动主程序时直接覆盖正式更新器，不创建 `.bak`；占用时重试，失败保留 `.pending`。禁止清单直接删除或编辑更新器，也禁止直接操作 `.pending`、`update_tmp`、`.media-update`、`updater_log.txt` 和 `.media-update-*` 内部路径。

路径不允许绝对路径、根目录本身、`.`/`..` 片段、Windows 设备名、流路径、通配符、结尾点或空格。运行时不再执行链接或 Windows 短文件名专项检查。清单生成工具仍拒绝链接和非常规文件。

## XML 结构修改：Sound Voltex 实例

本节依据 `spicetools.xml` 中实际的 `<game name="Sound Voltex">` 结构编写。假设游戏根目录为 `F:\Share\LAZY_KFC`，目标文件为 `contents/lazy/spicetools.xml`；清单的 `target` 必须写相对路径，不能写盘符。

### 先确认结构与定位

以下只摘录与示例有关的节点，省略其他游戏、按键、灯光和选项，不能用这段摘录覆盖完整配置：

```xml
<?xml version="1.0" encoding="utf-8"?>
<games>
    <game name="Sound Voltex">
        <buttons>
            <button name="BT-A" vkey="255" analogtype="0" debounce_up="0" debounce_down="0" velocity_threshold="0" invert="false" devid=""/>
        </buttons>
        <analogs>
            <analog name="VOL-L" index="255" sensivity="1" deadzone="0" devid="" deadzone_mirror="false" invert="false" smoothing="false" multiplier="1" relative="false" delay="0"/>
            <analog name="VOL-R" index="255" sensivity="1" deadzone="0" devid="" deadzone_mirror="false" invert="false" smoothing="false" multiplier="1" relative="false" delay="0"/>
        </analogs>
        <options>
            <option name="w" value="/ENABLED"/>
            <option name="sp2x-windowborder" value=""/>
            <option name="sp2x-windowalwaysontop" value=""/>
        </options>
    </game>
</games>
```

| 要定位的内容 | XPath |
|---|---|
| Sound Voltex 游戏节点 | `/games/game[@name='Sound Voltex']` |
| 该游戏的选项容器 | `/games/game[@name='Sound Voltex']/options` |
| `sp2x-windowborder` 选项元素 | `/games/game[@name='Sound Voltex']/options/option[@name='sp2x-windowborder']` |
| 该选项的 `value` 属性 | `/games/game[@name='Sound Voltex']/options/option[@name='sp2x-windowborder']/@value` |
| 左旋钮的 `sensivity` 属性 | `/games/game[@name='Sound Voltex']/analogs/analog[@name='VOL-L']/@sensivity` |
| BT-A 的 `vkey` 属性 | `/games/game[@name='Sound Voltex']/buttons/button[@name='BT-A']/@vkey` |

名称和属性区分大小写：游戏名必须是 `Sound Voltex`，不能改写成 `SOUND VOLTEX`；`sensivity` 是文件中的实际拼写，不能改成 `sensitivity`。每条 XPath 都限定游戏节点，避免选中其他游戏的同名配置。不要用 `[1]` 掩盖重复项；零匹配、多匹配都会使整包失败。

本文件通过属性保存这些配置值。例如 `<option name="w" value="/ENABLED"/>` 应修改 `@value`；对 `option` 元素使用 `setValue` 会写入元素文本，无法替代 `value` 属性。`/ENABLED` 是所提供文件中 `w` 的现有值，不应擅自换成通用示例里的 `true` 或 `1`。MediaUpdater 只校验 XML 结构，具体选项接受什么值仍由 Spice2x 决定。

### 可直接打包的修改示例

下面是完整 `update`，对应 [editXml 示例清单](examples/editXml/update)。它将 `sp2x-windowborder` 的 `value` 改为 `/ENABLED`，并将左旋钮 `sensivity` 改为 `1.2`。这些是展示写法的值，发布时按实际需求调整。

```json
{
  "operations": [
    {
      "type": "editXml",
      "target": "contents/lazy/spicetools.xml",
      "encoding": "auto",
      "edits": [
        {
          "action": "setValue",
          "xpath": "/games/game[@name='Sound Voltex']/options/option[@name='sp2x-windowborder']/@value",
          "value": "/ENABLED"
        },
        {
          "action": "setValue",
          "xpath": "/games/game[@name='Sound Voltex']/analogs/analog[@name='VOL-L']/@sensivity",
          "value": "1.2"
        }
      ]
    }
  ]
}
```

这个包只需要 `update` 和 `checksums`，无需 `source/`。两处目标及其属性必须已经存在；预演失败时不会留下部分修改。其他游戏和未声明修改的节点保留，序列化可能调整等价排版。

### 新增参数：addAttribute

“修改属性”与“新增属性”是两个动作。假设某个旧版本已有 `<option name="sp2x-windowborder"/>`，但没有 `value` 属性，可将以下对象放入 `edits`：

```json
{
  "action": "addAttribute",
  "xpath": "/games/game[@name='Sound Voltex']/options/option[@name='sp2x-windowborder']",
  "name": "value",
  "value": "/ENABLED"
}
```

所提供文件中 `value=""` 已经存在，即使是空字符串也不能使用 `addAttribute`，应使用上面的 `setValue`。若整个 `option` 不存在，则需新增元素。

### 新增配置行：appendChild、insertBefore、insertAfter

XML 中的一“行”是一个元素，不依赖它显示在哪一行。假设旧版本缺少 `sp2x-windowborder`，可在 `options` 末尾新增：

```json
{
  "action": "appendChild",
  "xpath": "/games/game[@name='Sound Voltex']/options[not(option[@name='sp2x-windowborder'])]",
  "xml": "<option name='sp2x-windowborder' value='/ENABLED'/>"
}
```

也可以放在已有 `w` 元素之前或之后；以下两个示例二选一：

```json
{
  "action": "insertBefore",
  "xpath": "/games/game[@name='Sound Voltex']/options[not(option[@name='sp2x-windowborder'])]/option[@name='w']",
  "xml": "<option name='sp2x-windowborder' value='/ENABLED'/>"
}
```

```json
{
  "action": "insertAfter",
  "xpath": "/games/game[@name='Sound Voltex']/options[not(option[@name='sp2x-windowborder'])]/option[@name='w']",
  "xml": "<option name='sp2x-windowborder' value='/ENABLED'/>"
}
```

`not(...)` 用于检查没有同名选项：如果已存在，XPath 匹配数为零，更新会失败，**不会跳过**。所提供文件已经有这个选项，因此这三段新增示例不适用于其当前状态。引擎不会自动判断 `name` 是否重复；省略这个条件可能插入重复选项。不要将三种新增方式同时放入一个包。

### 整行替换与删除：replaceElement、remove

替换已有选项元素，保持其原位置：

```json
{
  "action": "replaceElement",
  "xpath": "/games/game[@name='Sound Voltex']/options/option[@name='sp2x-windowborder']",
  "xml": "<option name='sp2x-windowborder' value='/ENABLED'/>"
}
```

替换会移除旧元素未包含在片段中的属性和子元素。如果只改一个属性，使用 `setValue` 即可，尤其不要为修改按键或旋钮的单个参数而覆盖整个节点。

删除整个 `sp2x-windowalwaysontop` 选项：

```json
{
  "action": "remove",
  "xpath": "/games/game[@name='Sound Voltex']/options/option[@name='sp2x-windowalwaysontop']"
}
```

仅删除该选项的 `value` 属性：

```json
{
  "action": "remove",
  "xpath": "/games/game[@name='Sound Voltex']/options/option[@name='sp2x-windowalwaysontop']/@value"
}
```

以上两种删除方式也是二选一，目标不存在时失败。删除属性可能形成不符合 Spice2x 配置要求的节点，仅在明确需要迁移该属性时使用；若要把现有值改为空字符串，应使用 `setValue` 和 `"value": ""`。

### 顺序、片段与命名空间规则

- 一个 `editXml` 的所有编辑放在 `edits` 数组中，按顺序执行；后续 XPath 读取前序修改结果。例如先删除属性再 `addAttribute` 可以成功，但普通改值无需这样做。
- 不自动创建文件、父节点或中间路径。禁止删除文档根元素 `games`，或在其前后插入第二个根元素。
- `xml` 必须是包含一个根元素的完整片段，可以包含嵌套结构和属性；外围仅允许空白，不允许 XML 声明或 DTD。JSON 字符串中的双引号需要写作 `\"`；本节用 XML 单引号属性减少转义。
- `value` 是普通字符串，XML 库负责转义 `&`、`<` 等字符。未知动作、旧 `line`/`match`/`text` 字段和不适用于当前动作的字段会被拒绝。
- 所提供的 `spicetools.xml` 没有 XML 命名空间，直接使用本节 XPath，无需填写 `namespaces`。对于其他带命名空间的 XML，可用操作级 `namespaces` 字典映射 XPath 前缀；默认命名空间也必须显式映射。新增属性的限定名使用该映射，插入片段自行声明命名空间，不允许通过属性操作修改 `xmlns`。

### 编码、排版与验证

- 可选 `encoding` 为 `auto`（默认）、`utf-8`、`utf-16le`、`utf-16be`、`gbk` 或 `shift-jis`。
- 自动模式根据 BOM、UTF-16 字节序特征和 XML 编码声明识别；没有编码信息时默认 UTF-8。支持这些编码的 XML 声明别名（如 `gb2312`、`shift_jis`），不猜测未声明的传统编码。
- 显式编码必须与 BOM、字节序及声明一致；编码冲突、非法字节和新增内容无法用原编码表示时失败。
- 保留原编码、BOM、XML 声明是否存在、注释、无关节点及末尾是否换行。新增元素沿用相邻缩进和文件换行风格，没有参考时使用四个空格与 CRLF。混合文本及 `xml:space='preserve'` 内容不会自动插入格式空白。
- XML 序列化可能改变等价的引号、实体、空元素写法或编码名称，不保证逐字节保留排版。安装后不保留原始字节备份。
- 文件与片段均禁用 DTD 和外部实体解析。全部 XML 修改先在内存中预演，最终序列化结果再次进行 XML 校验后才交给安装阶段直接写入。
- `editXml` 包需要支持此动作的 MediaUpdater，旧 `editText` 包不再支持。

## 四阶段更新流程

1. **启动器解压**：清理 `.media-update/update_tmp/` 后解压选中的包。
2. **启动器校验**：检查清单格式、基础路径范围、文件集合与逐文件 SHA-256。
3. **更新器预演**：生成内存中的最终变更列表，完成 XML 编辑；通过不截断文件的句柄探测检查源可读、目标可写或可删除，以及父目录创建权限。发现任何问题则整包不安装，不修改正式游戏文件。
4. **更新器安装**：先深层删除，再创建目录，最后直接复制文件或写入 XML；更新器 `.pending` 最后写入。

解压目录和日志位于游戏根目录：

```text
.media-update/
├── update_tmp/          # 解压内容；失败保留，成功尽力清理
└── updater_log.txt      # 本次启动器校验与更新器安装共同写入的日志
```

没有备份、载荷暂存副本、事务锁、交接摘要、进度持久化、回滚或启动恢复。预演只反映检查当时的状态；句柄检查后即释放。安装中出现新的占用、权限变化、空间不足或断电，可能留下部分更新，后续启动不会自动恢复或拦截。默认一次只执行一个更新，不协调并发更新。

更新各步骤不添加固定停顿。预演阶段可取消且不安装；安装阶段的 Ctrl+C 在当前文件操作结束后停止，不撤销已完成的修改。失败提示具体文件，保留解压内容和日志，不自动重新启动启动器。成功后显示绿色 `Update Successful!`，停留 5 秒再尝试重新启动；解压清理失败仅提示，不改变安装结果。

内部调用形式如下，`--parent-pid` 必须是发起更新的启动器进程 ID：

```text
MediaUpdater.exe --game "游戏根目录" --package "解压后的包根目录" --parent-pid 1234
```

旧 `--recover`、`--staging`、`--verification-sha256` 接口已删除，启动器与 MediaUpdater 必须成套部署。此开发分支旧版本遗留的事务和备份不会自动恢复、删除或阻止启动；它们也不参与新流程。

## 可运行示例包

`examples/` 包含四类独立示例源目录。安装 Python 3.10 或更新版本后，单独运行 `pwsh Packaging/New-ExamplePackages.ps1`，脚本会在临时目录生成每个示例的校验清单，再在 `build/update-examples/` 生成四个可选择的 ZIP 包。示例源目录不会被修改。`pwsh build.ps1` 只发布应用程序，不生成示例包。

在隔离的模拟游戏目录创建 `contents/lazy/` 与 `asphyxia/`，将自己的 `spicetools.xml` **副本**放到 `contents/lazy/spicetools.xml`；也可将本节开头的 XML 摘录保存为模拟文件。摘录只用于测试，不能覆盖真实完整配置。

按 `copy → mirror → editXml → delete` 顺序选择示例包。文件操作示例只操作 `contents/update-example`；XML 示例只修改 `contents/lazy/spicetools.xml` 中 Sound Voltex 的 `sp2x-windowborder/@value` 与 `VOL-L/@sensivity`，预期分别为 `/ENABLED`、`1.2`。再次安装同一个包会重新执行；可在两次安装之间修改目标值，验证重新安装会恢复为清单指定的值。

新增属性、新增元素、替换和删除的写法见上文；这些是具有各自前提的独立示例，不会全部包含在默认 ZIP 中。给不同初始配置制作包时，先用对应版本的副本验证目标存在性和唯一性，再生成校验清单并发布。

自动验证：`dotnet run --project Tests/RegressionTests/RegressionTests.csproj -c Release`。用例使用独立临时目录，包括真实文件占用、ACL 拒绝访问、安装中途失败、取消和子进程等待，不接触真实游戏数据。

生成工具测试：`python -B -m unittest discover -s Tests -p test_update_checksums.py -v`。设置环境变量 `UPDATE_TEST_WORKER` 为普通或 NativeAOT `RegressionTests.exe` 的绝对路径，可额外验证 Python 清单与 C# 校验、安装及 XML 编辑互通；设置 `UPDATE_TEST_UPDATER` 为发布后的 `MediaUpdater.exe` 的绝对路径，可验证四个示例 ZIP 的完整更新流程。这些用例均自动创建隔离模拟游戏目录；发布产物测试仅对子进程设置 `RunAsInvoker`，使用当前用户权限，不测试 UAC 提示、不修改发布程序的管理员启动设置。
