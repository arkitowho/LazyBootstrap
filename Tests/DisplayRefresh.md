# 显示器检测回归与实机验收

## 自动检查

```pwsh
dotnet run --project Tests/RegressionTests -c Release -- --display
dotnet run --project Tests/RegressionTests -c Release -- --display-live
dotnet run --project Tests/RegressionTests -c Release
dotnet build LazyBootstrap.sln -c Release
dotnet publish LazyBootstrap/LazyBootstrap.csproj -c Release -r win-x64 -o artifacts/display-refresh-aot
dotnet publish Tests/RegressionTests/RegressionTests.csproj -c Release -r win-x64 -o artifacts/display-regression-aot
& ./artifacts/display-regression-aot/RegressionTests.exe --display
& ./artifacts/display-regression-aot/RegressionTests.exe --display-live
```

`--display` 使用可控的枚举结果，覆盖补齐、去重、部分失败、空结果、身份匹配、手动刷新发现迟到的第二屏、事务互斥、游戏启动与运行期间暂停、在途和排队查询取消、退出清理后恢复及关闭取消。不会改变本机显示设置。

`--display-live` 只读调用实际 Windows API，连续十次检查启动器枚举包含活动桌面输出；不能代替真实热插拔测试。

## 多屏实机验收

1. 启动时读取一次显示器列表。Windows 使用扩展桌面，第二台显示器延迟开机后，点击“重新检测”，第二屏应进入主、副屏列表，无需重启启动器。
2. 拔插显示器、切换扩展/复制模式、进入显示器配置页、重新激活窗口或睡眠恢复均不应触发检测。通过“重新检测”按钮手动更新，不轮询，也不安排延迟重试。
3. 保存主、副屏和各自旋转、分辨率、刷新率，断开其中一屏并点击“重新检测”。对应选择应显示“暂未连接”，不能自动切到另一屏；刷新前后 `config.toml` 和 Spice XML 应保持原文。
4. 重新连接并刷新后应恢复原身份；若 Windows 输出编号改变，启动前的校验仍应匹配新编号并更新 Spice 配置。目标仍缺失时，预览和启动应停止并提示对应屏幕。
5. 复制模式共享输出只显示一个配置项。检测中按钮禁用，结束后恢复。设备尚未就绪时，可稍后再次点击“重新检测”。
6. 预览和还原事务期间不能手动刷新。预览选择“保持现状”或“还原”后，以及游戏退出等还原操作后，应在释放事务锁后刷新列表一次，不安排延迟重试；窗口关闭时跳过刷新。检测过程中关闭窗口，不应出现未处理异常或关闭后的界面更新。
7. 点击启动后，刷新按钮禁用并提示检测暂停。等待游戏启动及运行期间，不执行列表枚举或模式探测；还原后等显式刷新请求推迟到退出启动状态后处理。启动前必要的目标校验和显示设置应用仍在创建游戏进程之前完成。
8. 正常退出、崩溃、启动失败、取消启动和手动停止后，须等游戏进程退出且启动流程及显示设置还原完成，才恢复检测并统一刷新一次。Spice 自重启探测期间保持暂停。关闭启动器不恢复检测。

排查日志关注 `Display discovery refreshed` 的原因、状态、两个来源的数量及错误。`Display mapping` 在 Debug 级别记录持久身份到输出名称的映射。当前开发机只有一个活动输出，多屏物理场景尚需在对应硬件上验收。
