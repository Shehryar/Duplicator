# Duplicator
Screen Capture and Video Recording C# samples. There is only a Winform sample right now, but it should work with WPF.

Key points are:

* Uses SharpDX (for DirectX, DXGI, Direct2D and Media Foundation interop) and NAudio (only for sound capture and resampling) 
* Uses Desktop Duplication API for desktop duplication.
* Uses H264 for recording format.
* Uses zero CPU resources for desktop duplication
* Uses few resources for H264 encoding when a hardware encoder is available (Intel, etc.)
* Does not use GDI nor GDI+
