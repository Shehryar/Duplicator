# Duplicator
Screen Capture and Video Recording C# samples. There is only a Winform sample right now, but it should work with WPF because it only uses an Hwnd handle.

## Key points:

* Uses SharpDX exclusiverly for DirectX, DXGI, Direct2D and Media Foundation interop.
* Uses Desktop Duplication API for desktop duplication. It uses almost zero CPU and zero RAM.
* Uses an integrated custom optimized interop layer (over Windows Core Audio) for sound capture (loopback and microphone).
* Uses H264 + AAC for recording format.
* Uses few resources for H264 encoding when a hardware encoder is available (Intel (C) Media SDK, etc.).
* Never uses GDI nor GDI+ or legacy techology.

## Remarks

* The Frame Rate choice (which is optional, you can choose to use the `<Automatic>` mode) may influence the encoder choice (this is automatic). Some hardware encoders support only some frame rates. The choosen encoder will be displayed after recording has started.

![WinDuplicator.png](Duplicator/Doc/WinDuplicator.png?raw=true)
