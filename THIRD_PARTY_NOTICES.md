# Third-party notices

## MiBudsController

Redmi / Xiaomi 耳机协议实现参考并移植自：

- 项目：**MiBudsController**
- 作者：**FallenLeeee / MiBudsController Contributors**
- 仓库：https://github.com/FallenLeeee/MiBudsController
- 许可：MIT License

本仓库中以下文件包含其协议层代码或衍生实现：

- `wpf/src/PodsController.Wpf/Devices/Redmi/RedmiProtocol.cs`
- `wpf/src/PodsController.Wpf/Devices/Redmi/RedmiCatalog.cs`
- `wpf/src/PodsController.Wpf/Devices/Redmi/RedmiSession.cs`

原始项目为 Windows WinUI 3 应用；本仓库只移植 RCSP / SAFER+ / RFCOMM 相关协议逻辑，
并改为本项目的原生 RFCOMM 与 `EarbudSession` 结构。MiBudsController 是独立开源项目，
与小米 / Redmi 无隶属关系；本项目同样为非官方项目。

### MIT License

```text
MIT License

Copyright (c) 2026 FallenLeeee / MiBudsController Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
