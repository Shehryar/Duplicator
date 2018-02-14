# Duplicator
Screen Capture and Recording (Audio and Video) C# samples. There is only a Winform sample right now, but it should work with WPF or any Windows presentation technology because it only uses an Hwnd handle as a target.

## Key points:

* Uses SharpDX exclusively for DirectX, DXGI, Direct2D and Media Foundation interop.
* Uses Windows Desktop Duplication API for desktop duplication. It uses almost zero CPU and zero RAM.
* Uses an integrated custom optimized interop layer (over Windows Core Audio) for sound capture (loopback and microphone).
* Uses H264 + AAC for recording format.
* Uses few resources for H264 encoding when a hardware encoder is available (Intel (C) Media SDK, etc.).
* Never uses GDI nor GDI+ or legacy techology.

## Remarks

* It requires a recent Windows version (10+). Untested on other Windows versions.
* The Frame Rate choice (which is optional, you can choose to use the `<Automatic>` mode) may influence the encoder choice when more than one is available (this choice is automatic). Some hardware encoders support only some frame rates. The choosen encoder will be displayed by the app after recording has started.

![WinDuplicator.png](Duplicator/Doc/WinDuplicator.png?raw=true)
