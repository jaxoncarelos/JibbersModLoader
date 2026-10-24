"""Derive a platform-independent gameplay schema from the existing generated SDK metadata.
Does not edit generated bindings, read addresses, or weaken Windows build validation.
"""
from pathlib import Path
import html,json,re
root=Path(__file__).resolve().parents[1]
source=(root/'sdk/dotnet/Jibbers.Sdk/Generated/Interop.Assembly_CSharp.g.cs').read_text()
starts=list(re.finditer(r'public static readonly global::Jibbers.Sdk.Symbols.RawClassRef Class = new\("Assembly-CSharp.dll", "([^"\n]*)", "([^"\n]*)"\);',source))
classes={}
for i,match in enumerate(starts):
    names=re.findall(r'Raw binding of <c>(.*?)</c> \(Assembly-CSharp.dll',source[max(0,match.start()-1200):match.start()])
    if not names:raise ValueError('Missing generated class name')
    block=source[match.end():starts[i+1].start() if i+1<len(starts) else len(source)]
    fields=[{'name':html.unescape(n),'type':html.unescape(t),'static':bool(st)} for n,t,st in re.findall(r'/// <summary>Field `([^:`]+): ([^`]+)`( \(static\))?',block)]
    methods=[html.unescape(sig).replace(' ','') for sig in re.findall(r'/// <summary>`([^`]+)`\.</summary>',block) if ' -&gt; ' in sig and '(' in sig]
    classes[html.unescape(names[-1])]={'fields':fields,'methods':sorted(set(methods))}
assert len(classes)>500,'Incomplete reference schema'
(root/'mac/expected-schema.json').write_text(json.dumps(classes,separators=(',',':')))
print('Gameplay reference schema:',len(classes),'classes')
