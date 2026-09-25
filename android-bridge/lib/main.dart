import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:flutter_foreground_task/flutter_foreground_task.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:http/http.dart' as http;

const _defaultServer = 'https://kivra-labels.vercel.app';
const _tokenKey = 'bridge_token';
const _serverKey = 'bridge_server';
const _deviceNameKey = 'bridge_device_name';
const _completedJobsKey = 'completed_jobs';
const _version = '1.0.0';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  FlutterForegroundTask.initCommunicationPort();
  runApp(const KivraBridgeApp());
}

@pragma('vm:entry-point')
void startBridgeCallback() {
  DartPluginRegistrant.ensureInitialized();
  FlutterForegroundTask.setTaskHandler(PrintBridgeTaskHandler());
}

class BridgeApi {
  BridgeApi(this.server, this.token);

  final String server;
  final String token;

  Map<String, String> get _headers => {
    'content-type': 'application/json',
    'x-kivra-bridge-token': token,
  };

  Uri _uri(String path) =>
      Uri.parse('${server.replaceAll(RegExp(r'/$'), '')}$path');

  Future<Map<String, dynamic>> pair(String code, String deviceName) async {
    final response = await http
        .post(
          _uri('/api/bridge/pair'),
          headers: {'content-type': 'application/json'},
          body: jsonEncode({
            'code': code,
            'deviceName': deviceName,
            'appVersion': _version,
            'platform': 'Android',
          }),
        )
        .timeout(const Duration(seconds: 15));
    if (response.statusCode != 200) {
      throw BridgeException(
        response.statusCode == 401
            ? 'That pairing code is invalid or has expired.'
            : 'Pairing failed (${response.statusCode}).',
      );
    }
    return jsonDecode(response.body) as Map<String, dynamic>;
  }

  Future<void> heartbeat() async {
    final response = await http
        .post(
          _uri('/api/bridge/heartbeat'),
          headers: _headers,
          body: jsonEncode({'appVersion': _version}),
        )
        .timeout(const Duration(seconds: 12));
    _ensureAuthorized(response);
  }

  Future<Map<String, dynamic>?> claim() async {
    final response = await http
        .post(_uri('/api/bridge/jobs/claim'), headers: _headers, body: '{}')
        .timeout(const Duration(seconds: 15));
    _ensureAuthorized(response);
    if (response.statusCode == 204) return null;
    if (response.statusCode != 200) {
      throw BridgeException(
        'Could not claim a print job (${response.statusCode}).',
      );
    }
    return jsonDecode(response.body) as Map<String, dynamic>;
  }

  Future<void> complete(String id, bool success, [String? error]) async {
    final response = await http
        .post(
          _uri('/api/bridge/jobs/$id/complete'),
          headers: _headers,
          body: jsonEncode({'success': success, 'error': error}),
        )
        .timeout(const Duration(seconds: 15));
    _ensureAuthorized(response);
    if (response.statusCode != 204) {
      throw BridgeException(
        'Could not acknowledge the print job (${response.statusCode}).',
      );
    }
  }

  void _ensureAuthorized(http.Response response) {
    if (response.statusCode == 401) {
      throw const BridgeException(
        'This bridge has been disconnected. Pair it again.',
      );
    }
  }
}

class BridgeException implements Exception {
  const BridgeException(this.message);
  final String message;
  @override
  String toString() => message;
}

class PrintBridgeTaskHandler extends TaskHandler {
  static const _storage = FlutterSecureStorage(aOptions: AndroidOptions());
  bool _busy = false;
  DateTime _lastHeartbeat = DateTime.fromMillisecondsSinceEpoch(0);

  @override
  Future<void> onStart(DateTime timestamp, TaskStarter starter) async {
    await _poll();
  }

  @override
  void onRepeatEvent(DateTime timestamp) {
    if (!_busy) unawaited(_poll());
  }

  Future<void> _poll() async {
    _busy = true;
    try {
      final token = await _storage.read(key: _tokenKey);
      final server = await _storage.read(key: _serverKey) ?? _defaultServer;
      if (token == null || token.isEmpty) {
        await _publish('Pairing required', false);
        return;
      }

      final api = BridgeApi(server, token);
      if (DateTime.now().difference(_lastHeartbeat) >
          const Duration(seconds: 30)) {
        await api.heartbeat();
        _lastHeartbeat = DateTime.now();
      }

      final job = await api.claim();
      if (job == null) {
        await _publish('Ready for labels', true);
        return;
      }

      final id = job['jobId'] as String;
      final completed = await _completedJobs();
      if (!completed.contains(id)) {
        await _publish('Printing ${job['itemName']}', true);
        try {
          await _sendToPrinter(
            job['ipAddress'] as String,
            job['tcpPort'] as int,
            job['payload'] as String,
          );
          completed.add(id);
          while (completed.length > 100) {
            completed.remove(completed.first);
          }
          await FlutterForegroundTask.saveData(
            key: _completedJobsKey,
            value: jsonEncode(completed),
          );
        } catch (error) {
          await api.complete(id, false, _friendlyError(error));
          await _publish('Printer connection failed', false);
          return;
        }
      }

      await api.complete(id, true);
      await _publish('Printed ${job['itemName']}', true);
    } on BridgeException catch (error) {
      await _publish(error.message, false);
    } on SocketException catch (error) {
      await _publish(_friendlyError(error), false);
    } on TimeoutException {
      await _publish('Vercel is not reachable', false);
    } catch (error) {
      await _publish(_friendlyError(error), false);
    } finally {
      _busy = false;
    }
  }

  Future<List<String>> _completedJobs() async {
    final raw = await FlutterForegroundTask.getData<String>(
      key: _completedJobsKey,
    );
    if (raw == null || raw.isEmpty) return <String>[];
    return (jsonDecode(raw) as List).cast<String>();
  }

  Future<void> _sendToPrinter(String host, int port, String payload) async {
    final socket = await Socket.connect(
      host,
      port,
      timeout: const Duration(seconds: 8),
    );
    try {
      socket.add(utf8.encode(payload));
      await socket.flush();
      await Future<void>.delayed(const Duration(milliseconds: 250));
    } finally {
      await socket.close();
    }
  }

  Future<void> _publish(String message, bool online) async {
    await FlutterForegroundTask.updateService(
      notificationTitle: 'KIVRA Print Bridge',
      notificationText: message,
    );
    FlutterForegroundTask.sendDataToMain({
      'message': message,
      'online': online,
      'at': DateTime.now().toIso8601String(),
    });
  }

  String _friendlyError(Object error) {
    if (error is SocketException) {
      return 'Cannot reach the printer on local Wi-Fi';
    }
    final text = error.toString().replaceFirst('Exception: ', '');
    return text.length > 120 ? text.substring(0, 120) : text;
  }

  @override
  Future<void> onDestroy(DateTime timestamp, bool isTimeout) async {}

  @override
  void onReceiveData(Object data) {
    if (data == 'poll' && !_busy) unawaited(_poll());
  }

  @override
  void onNotificationButtonPressed(String id) {
    if (id == 'open') FlutterForegroundTask.launchApp('/');
  }

  @override
  void onNotificationPressed() => FlutterForegroundTask.launchApp('/');

  @override
  void onNotificationDismissed() {}
}

class KivraBridgeApp extends StatelessWidget {
  const KivraBridgeApp({super.key});

  @override
  Widget build(BuildContext context) {
    const ink = Color(0xff10242c);
    const red = Color(0xffef233c);
    return MaterialApp(
      title: 'KIVRA Print Bridge',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(
        useMaterial3: true,
        colorScheme: ColorScheme.fromSeed(
          seedColor: red,
          brightness: Brightness.light,
        ),
        scaffoldBackgroundColor: const Color(0xfff5f7f8),
        textTheme: Theme.of(
          context,
        ).textTheme.apply(bodyColor: ink, displayColor: ink),
        inputDecorationTheme: const InputDecorationTheme(
          filled: true,
          fillColor: Colors.white,
          border: OutlineInputBorder(
            borderRadius: BorderRadius.all(Radius.circular(8)),
          ),
          enabledBorder: OutlineInputBorder(
            borderRadius: BorderRadius.all(Radius.circular(8)),
            borderSide: BorderSide(color: Color(0xffd7e0e4)),
          ),
        ),
        cardTheme: const CardThemeData(
          color: Colors.white,
          elevation: 0,
          margin: EdgeInsets.zero,
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.all(Radius.circular(8)),
            side: BorderSide(color: Color(0xffd7e0e4)),
          ),
        ),
      ),
      routes: {'/': (_) => const BridgeHomePage()},
    );
  }
}

class BridgeHomePage extends StatefulWidget {
  const BridgeHomePage({super.key});

  @override
  State<BridgeHomePage> createState() => _BridgeHomePageState();
}

class _BridgeHomePageState extends State<BridgeHomePage> {
  static const _storage = FlutterSecureStorage(aOptions: AndroidOptions());
  final _serverController = TextEditingController(text: _defaultServer);
  final _codeController = TextEditingController();
  final _nameController = TextEditingController(text: 'Kitchen Print Bridge');
  bool _loading = true;
  bool _paired = false;
  bool _running = false;
  bool _working = false;
  String _status = 'Checking bridge';
  String? _error;

  @override
  void initState() {
    super.initState();
    FlutterForegroundTask.addTaskDataCallback(_onTaskData);
    _initialize();
  }

  Future<void> _initialize() async {
    FlutterForegroundTask.init(
      androidNotificationOptions: AndroidNotificationOptions(
        channelId: 'kivra_print_bridge',
        channelName: 'KIVRA Print Bridge',
        channelDescription:
            'Keeps this phone connected to the kitchen label printer.',
        onlyAlertOnce: true,
      ),
      iosNotificationOptions: const IOSNotificationOptions(
        showNotification: false,
      ),
      foregroundTaskOptions: ForegroundTaskOptions(
        eventAction: ForegroundTaskEventAction.repeat(5000),
        autoRunOnBoot: true,
        autoRunOnMyPackageReplaced: true,
        allowWakeLock: true,
        allowWifiLock: true,
        allowAutoRestart: true,
        stopWithTask: false,
      ),
    );

    final token = await _storage.read(key: _tokenKey);
    final server = await _storage.read(key: _serverKey);
    final name = await _storage.read(key: _deviceNameKey);
    final running = await FlutterForegroundTask.isRunningService;
    if (!mounted) return;
    setState(() {
      _paired = token != null && token.isNotEmpty;
      _running = running;
      _serverController.text = server ?? _defaultServer;
      _nameController.text = name ?? 'Kitchen Print Bridge';
      _status = running
          ? 'Ready for labels'
          : (_paired ? 'Bridge stopped' : 'Pair this phone');
      _loading = false;
    });
  }

  void _onTaskData(Object data) {
    if (data is! Map || !mounted) return;
    setState(() {
      _status = data['message']?.toString() ?? _status;
      _error = data['online'] == true ? null : _status;
      _running = true;
    });
  }

  Future<void> _pair() async {
    final code = _codeController.text.trim();
    final name = _nameController.text.trim();
    final server = _normaliseServer(_serverController.text);
    if (!RegExp(r'^\d{6}$').hasMatch(code) || name.isEmpty || server == null) {
      setState(
        () => _error =
            'Enter a six-digit code, device name, and valid HTTPS URL.',
      );
      return;
    }
    await _guard(() async {
      final result = await BridgeApi(server, '').pair(code, name);
      final token = result['token'] as String;
      await _storage.write(key: _tokenKey, value: token);
      await _storage.write(key: _serverKey, value: server);
      await _storage.write(key: _deviceNameKey, value: name);
      _codeController.clear();
      if (!mounted) return;
      setState(() {
        _paired = true;
        _status = 'Paired successfully';
      });
      await _start();
    });
  }

  Future<void> _start() async {
    final notification =
        await FlutterForegroundTask.checkNotificationPermission();
    if (notification != NotificationPermission.granted) {
      await FlutterForegroundTask.requestNotificationPermission();
    }
    if (Platform.isAndroid &&
        !await FlutterForegroundTask.isIgnoringBatteryOptimizations) {
      await FlutterForegroundTask.requestIgnoreBatteryOptimization();
    }

    final result = await FlutterForegroundTask.isRunningService
        ? FlutterForegroundTask.restartService()
        : FlutterForegroundTask.startService(
            serviceId: 7410,
            serviceTypes: const [ForegroundServiceTypes.connectedDevice],
            notificationTitle: 'KIVRA Print Bridge',
            notificationText: 'Ready for labels',
            notificationButtons: const [
              NotificationButton(id: 'open', text: 'Open'),
            ],
            callback: startBridgeCallback,
          );
    if (!mounted) return;
    setState(() {
      _running = result is ServiceRequestSuccess;
      _status = _running ? 'Ready for labels' : 'Could not start the bridge';
    });
  }

  Future<void> _stop() async {
    await FlutterForegroundTask.stopService();
    if (!mounted) return;
    setState(() {
      _running = false;
      _status = 'Bridge stopped';
    });
  }

  Future<void> _disconnect() async {
    await _stop();
    await _storage.deleteAll();
    await FlutterForegroundTask.clearAllData();
    if (!mounted) return;
    setState(() {
      _paired = false;
      _error = null;
      _status = 'Pair this phone';
    });
  }

  Future<void> _checkNow() async {
    FlutterForegroundTask.sendDataToTask('poll');
    setState(() => _status = 'Checking Vercel and printer queue');
  }

  Future<void> _guard(Future<void> Function() action) async {
    setState(() {
      _working = true;
      _error = null;
    });
    try {
      await action();
    } catch (error) {
      if (mounted) setState(() => _error = error.toString());
    } finally {
      if (mounted) setState(() => _working = false);
    }
  }

  String? _normaliseServer(String value) {
    final uri = Uri.tryParse(value.trim());
    if (uri == null || uri.scheme != 'https' || uri.host.isEmpty) return null;
    return uri.replace(path: uri.path.replaceAll(RegExp(r'/$'), '')).toString();
  }

  @override
  void dispose() {
    FlutterForegroundTask.removeTaskDataCallback(_onTaskData);
    _serverController.dispose();
    _codeController.dispose();
    _nameController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return WithForegroundTask(
      child: Scaffold(
        appBar: AppBar(
          titleSpacing: 20,
          toolbarHeight: 72,
          backgroundColor: Colors.white,
          surfaceTintColor: Colors.white,
          title: Row(
            children: [
              Image.asset(
                'assets/kivra-by-routes-logo.png',
                width: 92,
                height: 50,
                fit: BoxFit.contain,
              ),
              const SizedBox(width: 12),
              const Expanded(
                child: Text(
                  'Print Bridge',
                  style: TextStyle(fontSize: 18, fontWeight: FontWeight.w800),
                ),
              ),
            ],
          ),
        ),
        body: _loading
            ? const Center(child: CircularProgressIndicator())
            : ListView(
                padding: const EdgeInsets.all(20),
                children: [
                  _StatusPanel(
                    running: _running,
                    paired: _paired,
                    status: _status,
                  ),
                  if (_error != null) ...[
                    const SizedBox(height: 12),
                    MaterialBanner(
                      content: Text(_error!),
                      leading: const Icon(
                        Icons.error_outline,
                        color: Color(0xffc62828),
                      ),
                      backgroundColor: const Color(0xffffebee),
                      actions: [
                        TextButton(
                          onPressed: () => setState(() => _error = null),
                          child: const Text('Dismiss'),
                        ),
                      ],
                    ),
                  ],
                  const SizedBox(height: 24),
                  if (!_paired) _buildPairing() else _buildControls(),
                ],
              ),
      ),
    );
  }

  Widget _buildPairing() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          'Pair this phone',
          style: Theme.of(
            context,
          ).textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w800),
        ),
        const SizedBox(height: 6),
        const Text(
          'Generate a code in KIVRA under More > Android Print Bridge.',
        ),
        const SizedBox(height: 18),
        TextField(
          controller: _codeController,
          keyboardType: TextInputType.number,
          maxLength: 6,
          decoration: const InputDecoration(
            labelText: 'Pairing code',
            prefixIcon: Icon(Icons.pin_outlined),
            counterText: '',
          ),
        ),
        const SizedBox(height: 12),
        TextField(
          controller: _nameController,
          decoration: const InputDecoration(
            labelText: 'Device name',
            prefixIcon: Icon(Icons.phone_android_outlined),
          ),
        ),
        const SizedBox(height: 12),
        TextField(
          controller: _serverController,
          keyboardType: TextInputType.url,
          autocorrect: false,
          decoration: const InputDecoration(
            labelText: 'KIVRA server',
            prefixIcon: Icon(Icons.cloud_outlined),
          ),
        ),
        const SizedBox(height: 18),
        SizedBox(
          width: double.infinity,
          height: 52,
          child: FilledButton.icon(
            onPressed: _working ? null : _pair,
            icon: const Icon(Icons.link),
            label: Text(_working ? 'Pairing...' : 'Pair and start bridge'),
          ),
        ),
      ],
    );
  }

  Widget _buildControls() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          'Bridge controls',
          style: Theme.of(
            context,
          ).textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w800),
        ),
        const SizedBox(height: 14),
        SizedBox(
          width: double.infinity,
          height: 52,
          child: _running
              ? OutlinedButton.icon(
                  onPressed: _stop,
                  icon: const Icon(Icons.stop_circle_outlined),
                  label: const Text('Stop bridge'),
                )
              : FilledButton.icon(
                  onPressed: _start,
                  icon: const Icon(Icons.play_arrow),
                  label: const Text('Start bridge'),
                ),
        ),
        const SizedBox(height: 10),
        SizedBox(
          width: double.infinity,
          height: 48,
          child: OutlinedButton.icon(
            onPressed: _running ? _checkNow : null,
            icon: const Icon(Icons.sync),
            label: const Text('Check queue now'),
          ),
        ),
        const SizedBox(height: 26),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Row(
              children: [
                const Icon(Icons.cloud_done_outlined),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      const Text(
                        'Connected server',
                        style: TextStyle(fontWeight: FontWeight.w700),
                      ),
                      const SizedBox(height: 3),
                      Text(
                        _serverController.text,
                        style: const TextStyle(color: Color(0xff63747c)),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 20),
        TextButton.icon(
          onPressed: _disconnect,
          icon: const Icon(Icons.link_off),
          label: const Text('Disconnect this phone'),
        ),
      ],
    );
  }
}

class _StatusPanel extends StatelessWidget {
  const _StatusPanel({
    required this.running,
    required this.paired,
    required this.status,
  });
  final bool running;
  final bool paired;
  final String status;

  @override
  Widget build(BuildContext context) {
    final color = running ? const Color(0xff168447) : const Color(0xff708089);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(18),
        child: Row(
          children: [
            Container(
              width: 44,
              height: 44,
              decoration: BoxDecoration(
                color: color.withValues(alpha: .12),
                shape: BoxShape.circle,
              ),
              child: Icon(
                running ? Icons.print_outlined : Icons.print_disabled_outlined,
                color: color,
              ),
            ),
            const SizedBox(width: 14),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    running
                        ? 'Bridge running'
                        : (paired ? 'Bridge stopped' : 'Not paired'),
                    style: const TextStyle(
                      fontSize: 17,
                      fontWeight: FontWeight.w800,
                    ),
                  ),
                  const SizedBox(height: 3),
                  Text(
                    status,
                    style: const TextStyle(color: Color(0xff63747c)),
                  ),
                ],
              ),
            ),
            Container(
              width: 10,
              height: 10,
              decoration: BoxDecoration(color: color, shape: BoxShape.circle),
            ),
          ],
        ),
      ),
    );
  }
}
