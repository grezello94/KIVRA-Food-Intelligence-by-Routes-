# KIVRA Android Print Bridge

This companion app keeps an Android phone connected to the hosted KIVRA Labels
service and a TSC/TSPL network printer on the same local Wi-Fi.

## Pairing

1. In the KIVRA web app, open **More > Android Print Bridge** and generate a code.
2. Install and open this Android app.
3. Enter the six-digit code and tap **Pair and start bridge**.
4. Allow notifications and exclude the app from battery optimisation when asked.

The bridge polls Vercel over HTTPS, sends queued TSPL to the printer over TCP
port 9100, and acknowledges each job only after the socket write succeeds.

## Build

```sh
flutter pub get
flutter build apk --release
```

The APK is written to `build/app/outputs/flutter-apk/app-release.apk`.
