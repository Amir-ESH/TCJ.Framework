#!/usr/bin/env python3
from __future__ import annotations
import argparse,json,sys,xml.etree.ElementTree as ET
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
class VerificationError(RuntimeError): pass
def fail(m): raise VerificationError(m)
def readj(p):
 p=ROOT/p
 if not p.is_file(): fail(f"Required Kafka file is missing: {p.relative_to(ROOT)}")
 try:return json.loads(p.read_text(encoding='utf-8'))
 except json.JSONDecodeError as e: fail(f"Malformed JSON in {p.relative_to(ROOT)}: {e}")
def text(p):
 p=ROOT/p
 if not p.is_file(): fail(f"Required Kafka file is missing: {p.relative_to(ROOT)}")
 return p.read_text(encoding='utf-8')
def require(p,*markers):
 s=text(p); missing=[m for m in markers if m not in s]
 if missing: fail(f"{p} is missing required markers: {', '.join(missing)}")
def xml(p): return ET.parse(ROOT/p).getroot()
def refs(root,kind): return {x.attrib.get('Include','') for x in root.findall(f'.//{kind}')}
def validate():
 pol=readj('eng/kafka-policy.json'); con=readj('eng/kafka-contract.json')
 if pol.get('schemaVersion')!=1 or con.get('schemaVersion')!=1: fail('Kafka policy/contract schemaVersion must be 1.')
 if pol.get('packageName')!='TCJ.Messaging.Kafka' or con.get('packageName')!='TCJ.Messaging.Kafka': fail('Kafka package identity drifted.')
 image=pol.get('containerImage','')
 if not image.startswith('confluentinc/cp-kafka:') or image.endswith(':latest'): fail('Kafka image must be exactly pinned.')
 dp=xml('Directory.Packages.props'); versions={x.attrib['Include']:x.attrib.get('Version','') for x in dp.findall('.//PackageVersion') if 'Include' in x.attrib}
 if versions.get('Confluent.Kafka')!=pol.get('kafkaClientVersion'): fail('Confluent.Kafka central pin drifted.')
 if versions.get('Testcontainers.Kafka')!=pol.get('testcontainersKafkaVersion'): fail('Testcontainers.Kafka central pin drifted.')
 proj=xml('src/TCJ.Messaging.Kafka/TCJ.Messaging.Kafka.csproj')
 if refs(proj,'ProjectReference')!={'../TCJ.Messaging/TCJ.Messaging.csproj'} and {Path(x.replace('\\','/')).stem for x in refs(proj,'ProjectReference')}!={'TCJ.Messaging'}: fail('Kafka adapter must directly reference only TCJ.Messaging.')
 if 'Confluent.Kafka' not in refs(proj,'PackageReference'): fail('Kafka adapter must reference Confluent.Kafka.')
 arch=readj('eng/architecture-policy.json')
 if arch.get('assemblies',{}).get('TCJ.Messaging.Kafka')!=['TCJ.Messaging']: fail('Architecture policy must isolate Kafka.')
 if 'Confluent.Kafka' not in arch.get('forbiddenPublicApiTypePrefixes',{}).get('TCJ.Messaging.Kafka',[]): fail('Kafka SDK public API leakage must be forbidden.')
 for p in ['src/TCJ.Messaging/TCJ.Messaging.csproj','src/TCJ.AspNetCore/TCJ.AspNetCore.csproj','src/TCJ.EntityFrameworkCore/TCJ.EntityFrameworkCore.csproj','src/TCJ.EntityFrameworkCore.SqlServer/TCJ.EntityFrameworkCore.SqlServer.csproj']:
  if 'Confluent.Kafka' in refs(xml(p),'PackageReference'): fail(f'Kafka SDK leaked into {p}.')
 require('src/TCJ.Messaging.Kafka/Extensions/KafkaServiceCollectionExtensions.cs','AddTcjKafka','SupportsTransactions=false','SupportsDefer=false','OrderingGuarantee=MessagingOrderingGuarantee.PerPartition')
 require('src/TCJ.Messaging.Kafka/Configuration/TcjKafkaOptions.cs','EnableAutoCommit','EnableAutoOffsetStore','EnableIdempotence','AcknowledgementMode','TopologyMode')
 require('src/TCJ.Messaging.Kafka/Publishing/KafkaProducerManager.cs','EnableIdempotence=true','Acks=Acks.All','MessageSendMaxRetries')
 require('src/TCJ.Messaging.Kafka/Receiving/KafkaOffsetCoordinator.cs','NextOffset','Completed','stale after partition revocation')
 require('src/TCJ.Messaging.Kafka/Receiving/KafkaMessageSettlement.cs','PublishRetryAsync','PublishDeadAsync','MessagingCapabilityException("Abandon")','MessagingCapabilityException("Defer")')
 require('src/TCJ.Messaging.Kafka/Publishing/KafkaProducerManager.cs','EnableAutoCommit=false','EnableAutoOffsetStore=false')
 require('src/TCJ.Messaging.Kafka/Receiving/KafkaMessageReceiver.cs','Pause','Resume','Seek','Commit','ReceiveContext.Subscription')
 tests='\n'.join(p.read_text(encoding='utf-8') for p in (ROOT/'tests/TCJ.Messaging.Kafka.Tests').rglob('*.cs'))
 for marker in ['Contiguous','Unresolved','PartitionKey','OrderingKey','Rebalance','Pause_resume','Retry','Dead_letter','Shutdown','Inbox','Outbox','Conformance']:
  if marker.lower() not in tests.lower(): fail(f'Kafka tests are missing scenario marker: {marker}')
 if image not in tests: fail('Kafka tests must use the exact pinned image.')
 if tests.count('[Fact')<int(pol.get('minimumIntegrationTestCount',12)): fail('Kafka tests are below the policy minimum.')
 for fn,key in [('eng/release-manifest.json','releasePackages'),('eng/sbom-policy.json','releasePackages')]:
  d=readj(fn); ids=[x.get('id') for x in d[key]['runtime']]
  if 'TCJ.Messaging.Kafka' not in ids: fail(f'Kafka missing from {fn}.')
 for fn,key in [('eng/reproducibility-policy.json','requiredPackages'),('eng/coverage-policy.json','expectedPackages'),('eng/compatibility-policy.json','requiredPackages'),('eng/upgrade-compatibility-policy.json','requiredPackages')]:
  if 'TCJ.Messaging.Kafka' not in readj(fn).get(key,[]): fail(f'Kafka missing from {fn}.')
 aot=readj('eng/aot-policy.json')
 if not any(x.get('packageId')=='TCJ.Messaging.Kafka' and x.get('tier') in {'Unsupported','Conditional'} for x in aot.get('packages',[])): fail('Kafka AOT status must be conservative.')
 require('.github/workflows/kafka.yml','name: Kafka transport','eng/verify-kafka.py validate-config','TCJ.Messaging.Kafka.Tests','dotnet pack')
 require('.github/workflows/required-pr-gate.yml','kafka: ${{ steps.resolve.outputs.kafka }}',"if: needs.plan.outputs.kafka == 'true'",'uses: ./.github/workflows/kafka.yml','- kafka','"kafka":"${{ needs.kafka.result }}"')
 require('.github/workflows/ci.yml','eng/verify-kafka.py validate-config')
 require('.github/workflows/published-package-smoke.yml','kafka-smoke','EnableKafkaSmoke','TCJ_KAFKA_SMOKE')
 require('docs/messaging-kafka.md','at-least-once','contiguous','consumer-group','idempotence','Outbox','Inbox','AOT','ValidateOnly')
 require('compatibility/Consumers/KafkaPublisher.Console/KafkaPublisher.Console.csproj','TCJ.Messaging.Kafka','$(TCJCompatibilityVersion)')
 require('upgrade-tests/Scenarios/KafkaConsumer/KafkaConsumer.csproj','TCJ.Messaging.Kafka','$(TCJUpgradeVersion)')
 require('upgrade-tests/Expected/KafkaConsumer.json','KafkaConsumer','manualCommitDefault')
 return pol,con
def main():
 ap=argparse.ArgumentParser();ap.add_argument('command',choices=['validate-config']);a=ap.parse_args()
 try: validate(); print('Kafka configuration validation passed.'); return 0
 except VerificationError as e: print(f'Kafka verification failed: {e}',file=sys.stderr); return 1
if __name__=='__main__': raise SystemExit(main())
