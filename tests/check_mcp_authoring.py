"""STDIO通信を通した仕様取得・作成・診断・修正を検証します。"""
import hashlib
import json
import argparse
import queue
import subprocess
import threading
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RUN = ROOT / 'artifacts' / 'mcp-authoring' / uuid.uuid4().hex
RUN.mkdir(parents=True)
DLL = ROOT / 'artifacts/bin/UWSCSharp.Mcp/debug/UWSCSharp.Mcp.dll'
messages = queue.Queue()
checks = []
parser = argparse.ArgumentParser()
parser.add_argument('--source', type=Path)
parser.add_argument('--dll', type=Path, default=DLL)
parser.add_argument('--check-materials', action='store_true',
                    help='接続先が返す資料のハッシュを作業ツリーと照合します。')
options = parser.parse_args()
DLL = options.dll.resolve()


def check(name, condition):
    checks.append({'name': name, 'passed': bool(condition)})
    if not condition:
        raise AssertionError(name)


with (RUN / 'stderr.log').open('w', encoding='utf-8') as errors:
    process = subprocess.Popen(['dotnet', str(DLL)], cwd=RUN,
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=errors, text=True, encoding='utf-8')

    def read():
        for line in process.stdout:
            messages.put(json.loads(line))
        messages.put(None)

    threading.Thread(target=read, daemon=True).start()
    request_id = 0

    def send(value):
        process.stdin.write(json.dumps(value) + '\n')
        process.stdin.flush()

    def request(method, params):
        global request_id
        request_id += 1
        send({'jsonrpc': '2.0', 'id': request_id, 'method': method, 'params': params})
        while True:
            result = messages.get(timeout=15)
            if result is None:
                raise RuntimeError('サーバーが応答前に終了しました')
            if result.get('id') == request_id:
                return result

    def call(name, arguments):
        return request('tools/call', {'name': name, 'arguments': arguments})

    def content(response):
        check('tool-result', 'result' in response and not response['result'].get('isError'))
        return json.loads(response['result']['content'][0]['text'])

    analysis_records = []

    def analyze(label, source):
        response = content(call('analyze_script', {'source': source}))
        record = {'case': label, 'sourceChars': len(source), 'response': response}
        if len(source) < 2000:
            record['source'] = source
        analysis_records.append(record)
        return response

    try:
        initialized = request('initialize', {
            'protocolVersion': '2025-06-18', 'capabilities': {},
            'clientInfo': {'name': 'uws-authoring-check', 'version': '1.0'}})
        check('initialize', 'serverInfo' in initialized['result'])
        send({'jsonrpc': '2.0', 'method': 'notifications/initialized'})
        tools = request('tools/list', {})['result']['tools']
        check('five-tools', {x['name'] for x in tools} == {
            'search_reference', 'get_reference', 'get_capabilities',
            'validate_script', 'analyze_script'})
        found = content(call('search_reference', {'query': 'GETID', 'limit': 3}))
        check('search', any(x['name'] == 'GETID' for x in found['results']))
        reference = content(call('get_reference', {'name': 'getid'}))
        check('reference', reference['found'] and 'GETID(' in reference['reference']['signature'])
        (RUN / 'reference.json').write_text(json.dumps(reference, ensure_ascii=False, indent=2), encoding='utf-8')
        control_flow = content(call('get_reference', {'name': 'control-flow'}))
        check('syntax-document', control_flow['found'] and 'IFB' in control_flow['document'])
        check('syntax-search', any(x['name'] == 'control-flow' for x in content(
            call('search_reference', {'query': 'IFB'}))['results']))
        call_documents = content(call('search_reference', {'query': 'CALL'}))
        check('call-syntax-search', any(x['name'] == 'control-flow'
                                      for x in call_documents['results']))
        check('call-syntax-document', 'CALL child.uws(' in control_flow['document'])
        data_types = content(call('get_reference', {'name': 'data-types'}))
        check('array-declaration-document', data_types['found'] and
              'DIM values[1] = 10, 20' in data_types['document'] and
              '上限添字' in data_types['document'])
        guide = content(call('get_reference', {'name': 'authoring'}))
        check('authoring-document', guide['found'] and
              guide['evidence'] == 'docs/user-help/content/authoring.html' and
              all(term in guide['document'] for term in ['非ゼロ', 'FGET', 'CALL']))
        check('authoring-version', guide['version'] == reference['version'])
        guide_searches = []
        for query in ['作成支援', '入力条件', '終了コード']:
            search = content(call('search_reference', {'query': query}))
            check('authoring-search-' + query,
                  any(item['name'] == 'authoring' for item in search['results']))
            guide_searches.append({'query': query, 'response': search})
        (RUN / 'authoring-guide.json').write_text(json.dumps(
            {'reference': guide, 'searches': guide_searches},
            ensure_ascii=False, indent=2), encoding='utf-8')
        capabilities = content(call('get_capabilities', {'featureId': 'FN-GETID'}))
        check('capability', capabilities['found'] and capabilities['capability']['id'] == 'FN-GETID')
        if options.check_materials:
            for key, path in [
                ('referenceHash', ROOT / 'docs/user-help/reference.json'),
                ('ledgerHash', ROOT / 'docs/user-help/capabilities.json'),
            ]:
                source_text = path.read_bytes().decode('utf-8-sig').strip()
                expected_hash = hashlib.sha256(source_text.encode('utf-8')).hexdigest().upper()
                check('material-' + key, capabilities['version'][key] == expected_hash)
        # 取得した使用例からENDIFを落とし、診断後に原文へ戻します。
        source = reference['reference']['example']['code']
        check('example-has-endif', 'ENDIF' in source)
        broken = source.replace('ENDIF', '')
        diagnostic = content(call('validate_script', {'source': broken}))
        check('broken-diagnostic', diagnostic['valid'] is False and diagnostic['line'] > 0)
        fixed = content(call('validate_script', {'source': source}))
        check('fixed-example', fixed['valid'] is True)
        if options.source:
            custom_source = options.source.read_text(encoding='utf-8-sig')
            broken_custom = content(call('validate_script', {'source': custom_source.replace('ENDIF', '')}))
            check('skill-created-broken-diagnostic', not broken_custom['valid'] and broken_custom['line'] > 0)
            check('skill-created-source', content(call('validate_script', {'source': custom_source}))['valid'])
            (RUN / 'skill-created.uws').write_text(custom_source, encoding='utf-8-sig')
        (RUN / 'created.uws').write_text(source, encoding='utf-8-sig')
        for source in ['CALL missing_dependency', 'IFB FALSE\nCALL missing_dependency\nENDIF']:
            result = content(call('validate_script', {'source': source}))
            check('call-denied', result['valid'] is False)
        result = content(call('validate_script', {'source': 'PRINT NO_SUCH_FUNCTION()'}))
        check('semantic-limit-explicit', result['valid'] and '未知関数' in result['scope'])
        reviewed = analyze('unknown-and-missing', 'PRINT NO_SUCH_FUNCTION()\nGETID()')
        check('analysis-warnings', reviewed['syntaxValid'] and reviewed['callsAnalyzed']
              and [(x['line'], x['code']) for x in reviewed['diagnostics']]
              == [(1, 'unknown-function'), (2, 'argument-shape')]
              and all(x['severity'] == 'warning' for x in reviewed['diagnostics']))
        corrected = analyze('corrected-builtin', 'GETID("not-searched", , 0)')
        check('analysis-corrected', corrected['callsAnalyzed'] and not corrected['diagnostics'])
        routine = 'Sample()\nFUNCTION Sample(value)\nRESULT = value\nFEND'
        result = analyze('missing-routine-argument', routine)
        check('analysis-routine-warning', len(result['diagnostics']) == 1
              and result['diagnostics'][0]['code'] == 'argument-shape')
        result = analyze('corrected-routine', routine.replace('Sample()', 'Sample(1)', 1))
        check('analysis-routine-corrected', result['callsAnalyzed'] and not result['diagnostics'])
        result = analyze('dynamic-boundaries', 'EVAL("Missing()")\nPRINT obj.Value')
        check('analysis-boundaries', not result['diagnostics'] and result['uncheckedScopes']
              == {'memberAccesses': 1, 'dynamicCalls': 1, 'syntaxNodes': 0})
        result = analyze('syntax-error', 'IFB TRUE\nPRINT 1')
        check('analysis-syntax-failure', not result['syntaxValid'] and not result['callsAnalyzed']
              and result['line'] == 1 and result['uncheckedScopes'] is None)
        result = analyze('external-call', 'IFB FALSE\nCALL missing_dependency\nENDIF')
        check('analysis-call-denied', not result['syntaxValid'] and not result['callsAnalyzed']
              and result['line'] == 2 and '外部CALL' in result['message'])
        result = analyze('diagnostic-limit', '\n'.join(['Missing()'] * 101))
        check('analysis-truncated', result['truncated'] and len(result['diagnostics']) == 100)
        result = analyze('source-limit', ' ' * 64000)
        check('analysis-max-source', result['callsAnalyzed'] and not result['diagnostics'])
        check('analysis-limits-explicit', '実行成功を保証しません' in result['scope']
              and '動的コード' in result['scope'])
        check('not-found', not content(call('get_reference', {'name': 'NO_SUCH_FUNCTION'}))['found'])
        for name, arguments in [
            ('search_reference', {'query': '', 'limit': 1}),
            ('search_reference', {'query': 'GETID', 'limit': 21}),
            ('validate_script', {'source': 'x' * 64001}),
            ('validate_script', {}),
            ('analyze_script', {'source': 'x' * 64001}),
            ('analyze_script', {}),
            ('no_such_tool', {})
        ]:
            result = call(name, arguments)
            check('invalid-request-' + name, 'error' in result or result.get('result', {}).get('isError'))
        check('ping-after-errors', 'result' in request('ping', {}))
        send({'jsonrpc': '2.0', 'method': 'notifications/cancelled',
              'params': {'requestId': 99999, 'reason': '終了済み要求の取消'}})
        check('ping-after-unknown-cancel', 'result' in request('ping', {}))
        process.stdin.close()
        process.wait(timeout=15)
        check('clean-eof', process.returncode == 0)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
        (RUN / 'summary.json').write_text(json.dumps({
            'commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
            'dll': str(DLL), 'checks': checks, 'exit_code': process.returncode,
            'scope': '実際のSTDIO通信。例の作成は取得した仕様例に基づく。LLMの生成品質は別検証。'
        }, ensure_ascii=False, indent=2), encoding='utf-8')
        (RUN / 'call-analysis.json').write_text(json.dumps(
            analysis_records, ensure_ascii=False, indent=2), encoding='utf-8')
        print(RUN)
