import importlib.util, json, tempfile, unittest
from pathlib import Path
from xml.etree import ElementTree as ET

MODULE_PATH=Path(__file__).resolve().parents[1]/'verify-fuzzing.py'
spec=importlib.util.spec_from_file_location('verify_fuzzing',MODULE_PATH); vf=importlib.util.module_from_spec(spec); spec.loader.exec_module(vf)
SELECTOR_PATH=Path(__file__).resolve().parents[2]/'fuzz/scripts/select-fuzz-targets.py'
selector_spec=importlib.util.spec_from_file_location('select_fuzz_targets',SELECTOR_PATH); selector=importlib.util.module_from_spec(selector_spec); selector_spec.loader.exec_module(selector)

class FuzzingVerifierTests(unittest.TestCase):
    def test_repository_configuration_is_valid(self):
        vf.validate_config(skip_git=True)

    def test_property_suite_exceeds_minimum_and_has_replay(self):
        policy=vf.load_json(vf.POLICY_PATH); entries,categories=vf.inspect_properties(policy)
        self.assertGreaterEqual(len(entries),policy['minimumPropertyCount'])
        self.assertTrue(all(e['replay'] for e in entries))
        self.assertTrue(set(policy['requiredPropertyCategories']).issubset(categories))

    def test_property_replay_gamma_is_odd_and_seeds_are_unique(self):
        policy=vf.load_json(vf.POLICY_PATH); entries,_=vf.inspect_properties(policy)
        vf.validate_replay_seeds(entries)
        self.assertTrue(all(int(entry['replay'].split(',')[1]) % 2 == 1 for entry in entries))
        self.assertEqual(len(entries),len({entry['replay'] for entry in entries}))

    def test_even_fscheck_replay_gamma_is_rejected(self):
        entries=[{'name':'InvalidReplayProperty','replay':'1001,2002'}]
        with self.assertRaisesRegex(vf.VerificationError,'gamma must be odd'):
            vf.validate_replay_seeds(entries)

    def test_duplicate_property_replay_seed_is_rejected(self):
        entries=[
            {'name':'FirstProperty','replay':'1001,2001'},
            {'name':'SecondProperty','replay':'1001,2001'},
        ]
        with self.assertRaisesRegex(vf.VerificationError,'Duplicate property Replay seed'):
            vf.validate_replay_seeds(entries)

    def test_required_fuzz_targets_have_corpora(self):
        policy=vf.load_json(vf.POLICY_PATH); catalog=vf.load_json(vf.ROOT/policy['targetCatalog'])
        by_name={t['name']:t for t in catalog['targets']}
        for name in policy['requiredFuzzTargets']:
            self.assertIn(name,by_name); self.assertTrue((vf.ROOT/by_name[name]['corpus']).is_dir())

    def test_trx_parser_counts_pass_and_fail(self):
        with tempfile.TemporaryDirectory() as td:
            p=Path(td)/'r.trx'; p.write_text('<TestRun><Results><UnitTestResult outcome="Passed"/><UnitTestResult outcome="Failed"/></Results></TestRun>')
            self.assertEqual(vf.trx_results(Path(td))[:2],(1,1))

    def test_trx_parser_requires_results(self):
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(vf.VerificationError): vf.trx_results(Path(td))

    def test_sensitive_artifact_is_rejected(self):
        with tempfile.TemporaryDirectory() as td:
            p=Path(td); (p/'x.txt').write_text('github_pat_example')
            with self.assertRaises(vf.VerificationError): vf.scan_sensitive(p,['github_pat_'])

    def test_clean_artifact_is_allowed(self):
        with tempfile.TemporaryDirectory() as td:
            p=Path(td); (p/'x.txt').write_text('safe input')
            vf.scan_sensitive(p,['github_pat_'])

    def test_fuzz_result_contract_rejects_missing_file(self):
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(vf.VerificationError): vf.load_external(Path(td)/'missing.json')

    def test_fuzz_result_contract_rejects_malformed_json(self):
        with tempfile.TemporaryDirectory() as td:
            p=Path(td)/'bad.json'; p.write_text('{')
            with self.assertRaises(vf.VerificationError): vf.load_external(p)

    def test_required_projects_reference_only_src(self):
        policy=vf.load_json(vf.POLICY_PATH)
        vf.check_project(vf.ROOT/policy['propertyTestProject'],{'FsCheck.Xunit.v3'})
        vf.check_project(vf.ROOT/policy['fuzzProject'],{'SharpFuzz'})

    def test_central_tool_versions_exist(self):
        vf.check_central_versions()

    def test_workflow_contract_is_present(self):
        vf.check_workflows()

    def test_policy_requires_deterministic_seed_and_shrinking(self):
        policy=vf.load_json(vf.POLICY_PATH)
        self.assertTrue(policy['requireDeterministicSeed']); self.assertTrue(policy['requireShrinking'])

    def test_limits_are_positive(self):
        policy=vf.load_json(vf.POLICY_PATH)
        for key in ['maximumInputBytes','maximumCorpusEntryBytes','maximumSingleExecutionMilliseconds','maximumCollectionElements','maximumProcessMemoryBytes']:
            self.assertGreater(policy[key],0)

    def test_fuzz_duration_policy_is_bounded(self):
        policy=vf.load_json(vf.POLICY_PATH)
        self.assertGreaterEqual(policy['pullRequestFuzzSecondsPerTarget'],1)
        self.assertGreaterEqual(policy['scheduledFuzzMinutesPerTarget'],policy['pullRequestFuzzSecondsPerTarget']/60)

    def test_minimum_iterations_are_at_least_one_hundred(self):
        policy=vf.load_json(vf.POLICY_PATH); self.assertGreaterEqual(policy['minimumIterationsPerProperty'],100)


    def _write_fuzz_result(self, root, target, *, status='Pass', classification='Pass', duration=30, overrides=None):
        directory=Path(root)/target; directory.mkdir(parents=True,exist_ok=True)
        result={'target':target,'status':status,'seed':1,'durationSeconds':duration,'executions':10,'crashes':0,'hangs':0,
                'unexpectedExceptions':0,'invariantViolations':0,'inputSizeViolations':0,'timeoutViolations':0,
                'largestInputBytes':64,'peakWorkingSetBytes':1048576,'minimizedFailures':0,'unresolvedFailures':0,'failureKind':None,'failureHash':None}
        if overrides: result.update(overrides)
        (directory/'result.json').write_text(json.dumps(result))
        (directory/'runner-result.json').write_text(json.dumps({'target':target,'status':'Pass' if classification=='Pass' else 'Fail','classification':classification,'returnCode':0 if classification=='Pass' else 1}))


    def test_fuzz_scope_selects_only_affected_foundational_targets(self):
        selected=selector.select_targets([
            'src/TCJ.Core/Guards/Check.cs',
            'src/TCJ.Core/Results/Result.cs',
        ])
        self.assertEqual(selected,['Check','ResultComposition'])

    def test_fuzz_scope_uses_all_targets_for_fuzz_infrastructure(self):
        self.assertEqual(selector.select_targets(['fuzz/TCJ.FuzzTests/Program.cs']),selector.ALL_TARGETS)

    def test_verify_fuzz_accepts_selected_subset(self):
        with tempfile.TemporaryDirectory() as td, tempfile.TemporaryDirectory() as out:
            selected=['Check','ResultComposition']
            for target in selected: self._write_fuzz_result(td,target)
            vf.verify_fuzz(Path(td),Path(out),'abc',30,selected)
            summary=json.loads((Path(out)/'fuzz-summary.json').read_text())
            self.assertEqual(summary['targetCount'],2)

    def test_verify_fuzz_accepts_all_passing_targets(self):
        with tempfile.TemporaryDirectory() as td, tempfile.TemporaryDirectory() as out:
            policy=vf.load_json(vf.POLICY_PATH)
            for target in policy['requiredFuzzTargets']: self._write_fuzz_result(td,target)
            vf.verify_fuzz(Path(td),Path(out),'abc',30)
            self.assertTrue((Path(out)/'FUZZ_SUMMARY.md').is_file())

    def test_verify_fuzz_rejects_crash(self):
        with tempfile.TemporaryDirectory() as td, tempfile.TemporaryDirectory() as out:
            policy=vf.load_json(vf.POLICY_PATH)
            for target in policy['requiredFuzzTargets']: self._write_fuzz_result(td,target)
            broken=policy['requiredFuzzTargets'][0]
            self._write_fuzz_result(td,broken,status='Fail',overrides={'crashes':1,'unresolvedFailures':1,'failureKind':'Crash'})
            with self.assertRaises(vf.VerificationError): vf.verify_fuzz(Path(td),Path(out),'abc',30)

    def test_verify_fuzz_rejects_hang_watchdog(self):
        with tempfile.TemporaryDirectory() as td, tempfile.TemporaryDirectory() as out:
            policy=vf.load_json(vf.POLICY_PATH)
            for target in policy['requiredFuzzTargets']: self._write_fuzz_result(td,target)
            broken=policy['requiredFuzzTargets'][0]
            self._write_fuzz_result(td,broken,classification='Hang')
            with self.assertRaises(vf.VerificationError): vf.verify_fuzz(Path(td),Path(out),'abc',30)

    def test_verify_fuzz_rejects_short_duration(self):
        with tempfile.TemporaryDirectory() as td, tempfile.TemporaryDirectory() as out:
            policy=vf.load_json(vf.POLICY_PATH)
            for target in policy['requiredFuzzTargets']: self._write_fuzz_result(td,target,duration=2)
            with self.assertRaises(vf.VerificationError): vf.verify_fuzz(Path(td),Path(out),'abc',30)

    def test_verify_fuzz_rejects_missing_target(self):
        with tempfile.TemporaryDirectory() as td, tempfile.TemporaryDirectory() as out:
            policy=vf.load_json(vf.POLICY_PATH)
            for target in policy['requiredFuzzTargets'][1:]: self._write_fuzz_result(td,target)
            with self.assertRaises(vf.VerificationError): vf.verify_fuzz(Path(td),Path(out),'abc',30)

    def test_verify_properties_accepts_sufficient_passes(self):
        with tempfile.TemporaryDirectory() as td, tempfile.TemporaryDirectory() as out:
            policy=vf.load_json(vf.POLICY_PATH); entries,_=vf.inspect_properties(policy)
            results=''.join('<UnitTestResult outcome="Passed"/>' for _ in entries)
            (Path(td)/'p.trx').write_text(f'<TestRun><Results>{results}</Results></TestRun>')
            vf.verify_properties(Path(td),Path(out),'abc')
            self.assertTrue((Path(out)/'PROPERTY_TEST_SUMMARY.md').is_file())

    def test_verify_properties_rejects_failed_property(self):
        with tempfile.TemporaryDirectory() as td, tempfile.TemporaryDirectory() as out:
            policy=vf.load_json(vf.POLICY_PATH); entries,_=vf.inspect_properties(policy)
            results=''.join('<UnitTestResult outcome="Passed"/>' for _ in entries)+'<UnitTestResult outcome="Failed"/>'
            (Path(td)/'p.trx').write_text(f'<TestRun><Results>{results}</Results></TestRun>')
            with self.assertRaises(vf.VerificationError): vf.verify_properties(Path(td),Path(out),'abc')

    def test_missing_policy_is_rejected(self):
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(vf.VerificationError): vf.load_json(Path(td)/'missing-policy.json')

    def test_malformed_policy_is_rejected(self):
        with tempfile.TemporaryDirectory() as td:
            path=Path(td)/'policy.json'; path.write_text('{')
            with self.assertRaises(vf.VerificationError): vf.load_json(path)

if __name__=='__main__': unittest.main()
