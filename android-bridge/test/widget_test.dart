import 'package:flutter_test/flutter_test.dart';
import 'package:kivra_print_bridge/main.dart';

void main() {
  test('bridge errors expose a readable message', () {
    expect(
      const BridgeException('Printer offline').toString(),
      'Printer offline',
    );
  });
}
