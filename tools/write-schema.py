#!/usr/bin/env python3
"""Writes the explicit v1 wire schema. Validation runs separately against real output."""
import json,pathlib
root=pathlib.Path(__file__).resolve().parents[1]
def obj(properties):return dict(type='object',properties=properties,required=list(properties),additionalProperties=False)
def ref(name):return {'$ref':'#/$defs/'+name}
def arr(item):return dict(type='array',items=item)
def nullable(item):return {'anyOf':[item,{'type':'null'}]}
s={'type':'string'};i={'type':'integer','minimum':0};b={'type':'boolean'}
coverage={'enum':['read','inventoriedOnly','notRepresented','refused','notTested']}
sha={'type':'string','pattern':'^[0-9a-f]{64}$'}
source=obj(dict(partUri={'type':'string','pattern':'^/','maxLength':513},path={'type':'string','pattern':'^/'}))
options=['maxZipBytes','maxEntries','maxExpandedBytes','maxPartBytes','maxMediaBytes','maxMedia','maxImagePixels','maxExpansionRatio','maxDepth','maxNodes','maxAttributesPerNode','maxValueChars','maxAllocatedBytes','maxWorkingSetBytes','maxOutputBytes','maxDurationMilliseconds']
defs=dict(source=source,coverage=coverage,budgets=obj({k:{'type':'integer','minimum':1} for k in options}),
 node=obj(dict(id={'type':'string','pattern':'^n[0-9]{6,10}$'},parentId=nullable(s),source=ref('source'),name=s,kind=s,coverage=ref('coverage'),attributes={'type':'object','additionalProperties':s},text=nullable(s),revision={'enum':['unchanged','inserted','deleted']},fieldRole={'enum':['none','instruction','result']})),
 part=obj(dict(partUri=s,contentType=s,sha256=sha,bytes=i,coverage=ref('coverage'),nodeCount=i)),
 relationship=obj(dict(sourcePart=s,id=s,type=s,target=s,mode={'enum':['Internal','External']},resolvedPart=nullable(s))),
 media=obj(dict(id={'type':'string','pattern':'^sha256-[0-9a-f]{64}$'},partUri=s,sha256=sha,bytes=i,contentType=s,coverage=ref('coverage'),width=nullable(i),height=nullable(i))),
 style=obj(dict(nodeId=s,styleId=s,ancestors=arr(s),coverage=ref('coverage'))),
 diagnostic=obj(dict(code=s,severity={'enum':['info','warning','error']},coverage=ref('coverage'),source=nullable(ref('source')),message=s)))
schema=obj(dict(schemaVersion={'const':1},engineVersion=s,format=s,status=ref('coverage'),inspectionComplete=b,importReady={'const':False},pagination={'const':'notTested'},sourceSha256=nullable(sha),budgets=ref('budgets'),parts=arr(ref('part')),nodes=arr(ref('node')),relationships=arr(ref('relationship')),media=arr(ref('media')),styles=arr(ref('style')),diagnostics=arr(ref('diagnostic'))))
schema.update({'$schema':'https://json-schema.org/draft/2020-12/schema','$id':'urn:lecteur-word:manifest:1','$defs':defs,'title':'Lecteur Word inspection manifest v1'})
(root/'docs/manifest-v1.schema.json').write_text(json.dumps(schema,indent=2)+'\n')
