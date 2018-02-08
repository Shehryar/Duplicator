# Duplicator
Screen Capture and Video Recording C# samples. There is only a Winform sample right now, but it should work with WPF.

Key points are:

* Uses SharpDX exclusiverly for DirectX, DXGI, Direct2D and Media Foundation interop.
* Uses Desktop Duplication API for desktop duplication. It uses almost zero CPU and zero RAM.
* Uses an integrated custom optimized interop layer (over Windows Core Audio) for sound capture.
* Uses H264 + AAC for recording format.
* Uses few resources for H264 encoding when a hardware encoder is available (Intel, etc.).
* Never uses GDI nor GDI+ or legacy techology.
