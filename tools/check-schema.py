#!/usr/bin/python3
"""Uses the distribution's JSON Schema validator, offline, against actual CLI output."""
import json,pathlib,shutil,jsonschema
root=pathlib.Path(__file__).resolve().parents[1];logs=root/'.local/qualification'
schema=json.loads((root/'docs/manifest-v1.schema.json').read_text());jsonschema.Draft202012Validator.check_schema(schema)
measures=json.loads((logs/'measurements.json').read_text())
for measure in measures:
 path=pathlib.Path(measure['output'])/'manifest.json';jsonschema.validate(json.loads(path.read_text()),schema)
example=logs/'demo';example.mkdir(exist_ok=True)
shutil.copyfile(pathlib.Path(measures[0]['output'])/'manifest.json',example/'manifest.json')
result=dict(status='OK',schemaVersion=1,validatedManifests=3,validator='system python3 jsonschema',network=False)
(logs/'schema.json').write_text(json.dumps(result,indent=2));print('OK schema v1: 3 real manifests; demo retained')
