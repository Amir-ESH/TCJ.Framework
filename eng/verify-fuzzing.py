#!/usr/bin/env python3
from __future__ import annotations
import argparse, json, re, subprocess, sys
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT=Path(__file__).resolve().parents[1]
POLICY_PATH=ROOT/'eng/fuzzing-policy.json'
PROPERTY_RE=re.compile(r'\[Property\((?P<args>.*?)\)\]',re.S)
REPLAY_RE=re.compile(r'Replay\s*=\s*"(?P<seed>[0-9]+,[0-9]+(?:,[0-9]+)?)"')
MAX_RE=re.compile(r'MaxTest\s*=\s*(?P<count>[0-9]+)')
TRAIT_RE=re.compile(r'\[Trait\("Category",\s*"(?P<category>[^"]+)"\)\]')

class VerificationError(RuntimeError): pass

def display_path(path:Path):
    try: return str(path.relative_to(ROOT))
    except ValueError: return str(path)

def load_json(path:Path):
    try: return json.loads(path.read_text(encoding='utf-8'))
    except FileNotFoundError as e: raise VerificationError(f'Missing required file: {display_path(path)}') from e
    except json.JSONDecodeError as e: raise VerificationError(f'Malformed JSON: {display_path(path)}: {e}') from e

def require(condition,message):
    if not condition: raise VerificationError(message)

def property_sources(policy):
    project=ROOT/policy['propertyTestProject']; require(project.is_file(),f"Missing property-test project: {project.relative_to(ROOT)}")
    return sorted(project.parent.glob('*.cs'))+sorted((project.parent/'Infrastructure').glob('*.cs'))

def inspect_properties(policy):
    entries=[]; categories=set()
    for path in property_sources(policy):
        text=path.read_text(encoding='utf-8')
        categories.update(TRAIT_RE.findall(text))
        for match in PROPERTY_RE.finditer(text):
            args=match.group('args'); replay=REPLAY_RE.search(args); max_test=MAX_RE.search(args)
            tail=text[match.end():match.end()+500]
            method=re.search(r'public\s+(?:async\s+)?(?:bool|void|Task(?:<[^>]+>)?)\s+(\w+)\s*\(',tail)
            entries.append({'file':str(path.relative_to(ROOT)),'name':method.group(1) if method else '<unknown>',
                            'replay':replay.group('seed') if replay else None,
                            'iterations':int(max_test.group('count')) if max_test else 0})
    return entries,categories

def validate_replay_seeds(entries):
    seen=set()
    for entry in entries:
        replay=entry['replay']
        require(replay,f"Property {entry['name']} is missing deterministic Replay seed.")
        parts=[int(part) for part in replay.split(',')]
        require(len(parts)>=2,f"Property {entry['name']} has an invalid Replay seed: {replay}.")
        require(parts[1] % 2 == 1,
                f"Property {entry['name']} has invalid FsCheck Replay gamma {parts[1]}; gamma must be odd.")
        require(replay not in seen,f"Duplicate property Replay seed: {replay}.")
        seen.add(replay)

def check_central_versions():
    root=ET.parse(ROOT/'Directory.Packages.props').getroot()
    versions={e.attrib.get('Include'):e.attrib.get('Version') for e in root.findall('.//PackageVersion')}
    require(versions.get('FsCheck.Xunit.v3'),'FsCheck.Xunit.v3 must be centrally versioned.')
    require(versions.get('SharpFuzz'),'SharpFuzz must be centrally versioned.')

def check_project(path:Path, required_packages):
    tree=ET.parse(path); tf=tree.find('.//TargetFramework')
    # The repository targets net10.0 globally; an explicit project TFM is allowed but not required.
    if tf is not None: require(tf.text=='net10.0',f'{path.relative_to(ROOT)} must target net10.0.')
    refs={e.attrib.get('Include') for e in tree.findall('.//PackageReference')}
    for package in required_packages: require(package in refs,f'{path.relative_to(ROOT)} must reference {package}.')
    for ref in tree.findall('.//ProjectReference'):
        target=(path.parent/ref.attrib['Include'].replace('\\','/')).resolve()
        require(str(target).startswith(str((ROOT/'src').resolve())),f'Unexpected project reference in {path.relative_to(ROOT)}: {ref.attrib["Include"]}')

def check_git(policy,skip):
    if skip or not (ROOT/'.git').exists(): return
    tracked=[POLICY_PATH,ROOT/policy['targetCatalog']]
    tracked += list((ROOT/'fuzz/corpus').rglob('*'))
    tracked += list((ROOT/'tests/TCJ.PropertyTests').rglob('*.cs'))
    generated=subprocess.run(['git','ls-files','artifacts/fuzzing','fuzz/generated-corpus'],cwd=ROOT,text=True,capture_output=True,check=False)
    require(not generated.stdout.strip(),'Generated fuzz output must not be tracked by Git.')
    for path in tracked:
        if not path.is_file(): continue
        proc=subprocess.run(['git','check-ignore','-q',str(path.relative_to(ROOT))],cwd=ROOT)
        require(proc.returncode!=0,f'Required fuzzing file is ignored by Git: {path.relative_to(ROOT)}')

def check_workflows():
    required=['.github/workflows/fuzzing.yml','.github/workflows/ci.yml','.github/workflows/release-preflight.yml','.github/workflows/release.yml']
    for rel in required: require((ROOT/rel).is_file(),f'Missing workflow integration: {rel}')
    fuzz=(ROOT/'.github/workflows/fuzzing.yml').read_text(encoding='utf-8')
    for token in ['Property and fuzz testing','Run property tests','Run short fuzz targets','Run scheduled fuzz targets','verify-fuzzing.py validate-config','verify-properties','verify-fuzz','select-fuzz-targets.py']:
        require(token in fuzz,f'Fuzz workflow is missing required contract: {token}')
    ci=(ROOT/'.github/workflows/ci.yml').read_text(encoding='utf-8')
    require('verify-fuzzing.py validate-config' in ci,'Normal CI must validate fuzzing configuration.')
    require('TCJ.PropertyTests.csproj' in ci,'Normal CI must run property tests.')
    for rel in ['.github/workflows/release-preflight.yml','.github/workflows/release.yml']:
        text=(ROOT/rel).read_text(encoding='utf-8')
        require('verify-fuzzing.py validate-config' in text,f'{rel} must validate fuzzing configuration.')
        require('run-fuzz.py' in text and 'verify-fuzz' in text,f'{rel} must block on fuzzing results.')

def validate_config(skip_git=False):
    policy=load_json(POLICY_PATH)
    require(policy.get('schemaVersion')==1,'Unsupported fuzzing policy schemaVersion.')
    check_central_versions()
    prop=ROOT/policy['propertyTestProject']; fuzz=ROOT/policy['fuzzProject']
    check_project(prop,{'FsCheck.Xunit.v3'})
    check_project(fuzz,{'SharpFuzz'})
    entries,categories=inspect_properties(policy)
    require(len(entries)>=int(policy['minimumPropertyCount']),f"Property count {len(entries)} is below required {policy['minimumPropertyCount']}.")
    missing=set(policy['requiredPropertyCategories'])-categories
    require(not missing,f'Missing required property categories: {sorted(missing)}')
    for entry in entries:
        require(entry['iterations']>=int(policy['minimumIterationsPerProperty']),f"Property {entry['name']} has insufficient iterations.")
    validate_replay_seeds(entries)
    generators=(prop.parent/'Infrastructure/PropertyArbitraries.cs').read_text(encoding='utf-8')
    require('Arb.From' in generators and 'Shrink' in generators,'Custom generators with shrinking are required.')
    catalog=load_json(ROOT/policy['targetCatalog']); names={item['name'] for item in catalog.get('targets',[])}
    required_targets=set(policy['requiredFuzzTargets']); require(required_targets<=names,f'Missing required fuzz targets: {sorted(required_targets-names)}')
    for item in catalog['targets']:
        corpus=(ROOT/item['corpus']).resolve(); require(str(corpus).startswith(str((ROOT/'fuzz/corpus').resolve())),f'Unsafe corpus path for {item["name"]}.')
        require(corpus.is_dir(),f'Missing corpus for {item["name"]}.')
        for entry in corpus.iterdir():
            if entry.is_file():
                require(entry.suffix.lower() in {'.txt','.bin','.json'},f'Unexpected executable or unsupported corpus file: {entry.relative_to(ROOT)}')
                require((entry.stat().st_mode & 0o111)==0,f'Executable corpus file is not allowed: {entry.relative_to(ROOT)}')
                require(entry.stat().st_size<=int(policy['maximumCorpusEntryBytes']),f'Oversized corpus entry: {entry.relative_to(ROOT)}')
                data=entry.read_bytes().decode('utf-8',errors='ignore')
                for marker in policy['sensitiveMarkers']: require(marker.lower() not in data.lower(),f'Sensitive marker in corpus: {entry.relative_to(ROOT)}')
    runner=(ROOT/'fuzz/scripts/run-fuzz.py').read_text(encoding='utf-8')
    require('timeout=args.duration+15' in runner,'External total target watchdog is required.')
    campaign=(fuzz.parent/'FuzzCampaign.cs').read_text(encoding='utf-8')
    require('Task.WhenAny' in campaign and 'maxInputBytes' in campaign and 'maximumProcessMemoryBytes' in campaign,'Per-input timeout, input-size, and memory limits are required.')
    require('minimized' in campaign and 'failures' in campaign,'Failure corpus and minimization support are required.')
    enum_target=(fuzz.parent/'Targets/EnumerableExtensionsTarget.cs').read_text(encoding='utf-8')
    require(f"MaxElements = {policy['maximumCollectionElements']}" in enum_target,'Fuzz collection limit must match policy.')
    check_workflows(); check_git(policy,skip_git)
    print(f"Fuzzing configuration is valid: properties={len(entries)}, categories={len(categories)}, targets={len(required_targets)}, minIterations={policy['minimumIterationsPerProperty']}.")

def trx_results(directory:Path):
    passed=failed=0
    files=list(directory.rglob('*.trx')); require(files,f'No TRX files found under {directory}.')
    for file in files:
        root=ET.parse(file).getroot()
        for node in root.iter():
            if node.tag.endswith('UnitTestResult'):
                outcome=(node.attrib.get('outcome') or '').lower()
                if outcome=='passed': passed+=1
                elif outcome in {'failed','error','timeout','aborted'}: failed+=1
    return passed,failed,len(files)

def verify_properties(results:Path,output:Path,commit_sha:str):
    policy=load_json(POLICY_PATH); entries,categories=inspect_properties(policy); passed,failed,trx_count=trx_results(results)
    require(failed==0,f'Property test results contain {failed} failing test(s).')
    require(passed>=len(entries),f'Only {passed} passing test results were recorded for {len(entries)} properties.')
    output.mkdir(parents=True,exist_ok=True)
    total=sum(e['iterations'] for e in entries)
    summary={'sourceCommit':commit_sha,'propertyCount':len(entries),'categories':sorted(c for c in categories if c!='Property'),
             'totalGeneratedCases':total,'minimumIterations':min(e['iterations'] for e in entries),'failingPropertyCount':failed,
             'seedValues':[e['replay'] for e in entries],'shrinkingEnabled':True,'replayStatus':'Available','trxFiles':trx_count,'overall':'PASS'}
    (output/'property-test-summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
    md=['# Property test summary','',f'- Source commit: `{commit_sha or "local"}`',f'- Property count: {len(entries)}',f'- Categories: {", ".join(summary["categories"])}',
        f'- Generated cases (configured): {total}',f'- Minimum iterations/property: {summary["minimumIterations"]}',f'- Failing properties: {failed}','- Shrinking: enabled','- Deterministic replay: available from each pinned `Replay` seed and FsCheck failure output','- Overall: **PASS**','']
    (output/'PROPERTY_TEST_SUMMARY.md').write_text('\n'.join(md),encoding='utf-8')
    print(f'Property verification passed: {len(entries)} properties, >= {total} generated cases configured.')

def scan_sensitive(path:Path,markers):
    for file in path.rglob('*'):
        if file.is_file() and file.stat().st_size<=2_000_000:
            text=file.read_bytes().decode('utf-8',errors='ignore').lower()
            for marker in markers:
                if marker.lower() in text: raise VerificationError(f'Sensitive marker found in fuzz artifact: {file}')

def verify_fuzz(results:Path,output:Path,commit_sha:str,minimum_duration:int|None,targets:list[str]|None=None):
    policy=load_json(POLICY_PATH); required=targets or policy['requiredFuzzTargets']; rows=[]
    unknown=set(required)-set(policy['requiredFuzzTargets']); require(not unknown,f'Unknown fuzz targets requested for verification: {sorted(unknown)}')
    for name in required:
        directory=results/name; require(directory.is_dir(),f'Missing fuzz target results: {name}')
        runner=load_external(directory/'runner-result.json')
        require(runner.get('status')=='Pass',f'Fuzz target process failed: {name} ({runner.get("classification")})')
        data=load_external(directory/'result.json')
        require(data.get('status')=='Pass',f'Fuzz target failed: {name}: {data.get("failureKind")}')
        for key in ['crashes','hangs','unexpectedExceptions','invariantViolations','inputSizeViolations','timeoutViolations','unresolvedFailures']:
            require(int(data.get(key,0))==0,f'{name} reports {key}={data.get(key)}')
        require(int(data.get('executions',0))>0,f'{name} did not execute any inputs.')
        require(int(data.get('largestInputBytes',0))<=int(policy['maximumInputBytes']),f'{name} exceeded the maximum input-size policy.')
        require(int(data.get('peakWorkingSetBytes',0))<=int(policy['maximumProcessMemoryBytes']),f'{name} exceeded the process-memory policy.')
        if minimum_duration is not None:
            require(float(data.get('durationSeconds',0))>=max(0,minimum_duration-1),f'{name} did not run for the required duration.')
        rows.append(data)
    scan_sensitive(results,policy['sensitiveMarkers'])
    output.mkdir(parents=True,exist_ok=True)
    summary={'sourceCommit':commit_sha,'targetCount':len(rows),'durationPerTargetSeconds':[round(float(r['durationSeconds']),3) for r in rows],
             'totalFuzzSeconds':round(sum(float(r['durationSeconds']) for r in rows),3),'totalExecutions':sum(int(r['executions']) for r in rows),'largestInputBytes':max(int(r.get('largestInputBytes',0)) for r in rows),'peakWorkingSetBytes':max(int(r.get('peakWorkingSetBytes',0)) for r in rows),'uniqueCrashes':0,'uniqueHangs':0,'unexpectedExceptions':0,
             'minimizedFailures':0,'unresolvedCorpusCount':0,'inputSizeLimitViolations':0,'timeoutLimitViolations':0,'overall':'PASS'}
    (output/'fuzz-summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
    md=['# Fuzz summary','',f'- Source commit: `{commit_sha or "local"}`',f'- Target count: {len(rows)}',f'- Total fuzz time: {summary["totalFuzzSeconds"]} seconds',f'- Total executions: {summary["totalExecutions"]}',f'- Largest input observed: {summary["largestInputBytes"]} bytes',f'- Peak working set observed: {summary["peakWorkingSetBytes"]} bytes',
        '- Unique crashes: 0','- Unique hangs: 0','- Unexpected exceptions: 0','- Invariant violations: 0','- Input-size violations: 0','- Timeout violations: 0','- Unresolved corpus: 0','- Overall: **PASS**','']
    (output/'FUZZ_SUMMARY.md').write_text('\n'.join(md),encoding='utf-8')
    print(f'Fuzz verification passed: targets={len(rows)}, executions={summary["totalExecutions"]}.')

def load_external(path:Path):
    try: return json.loads(path.read_text(encoding='utf-8'))
    except FileNotFoundError as e: raise VerificationError(f'Missing fuzz result file: {path}') from e
    except json.JSONDecodeError as e: raise VerificationError(f'Malformed fuzz result file: {path}: {e}') from e

def main():
    parser=argparse.ArgumentParser(); sub=parser.add_subparsers(dest='command',required=True)
    p=sub.add_parser('validate-config'); p.add_argument('--skip-git-check',action='store_true')
    p=sub.add_parser('verify-properties'); p.add_argument('--results',required=True); p.add_argument('--output',required=True); p.add_argument('--commit-sha',default='')
    p=sub.add_parser('verify-fuzz'); p.add_argument('--results',required=True); p.add_argument('--output',required=True); p.add_argument('--commit-sha',default=''); p.add_argument('--minimum-duration-seconds',type=int); p.add_argument('--target',action='append')
    args=parser.parse_args()
    try:
        if args.command=='validate-config': validate_config(args.skip_git_check)
        elif args.command=='verify-properties': verify_properties(Path(args.results),Path(args.output),args.commit_sha)
        else: verify_fuzz(Path(args.results),Path(args.output),args.commit_sha,args.minimum_duration_seconds,args.target)
        return 0
    except (VerificationError,ET.ParseError,ValueError) as e:
        print(f'Fuzzing verification failed: {e}',file=sys.stderr); return 1
if __name__=='__main__': raise SystemExit(main())
