#!/usr/bin/env python3
from __future__ import annotations
import json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'parser'))
from b3d_parser import parse_current_model, decode_contour_blob


def fields(n): return {c.name:c for c in (n.children or [])}
def scalar(f, *names, default=None):
    for k in names:
        if k in f: return f[k].value
    return default

def children_named(n, name): return [c for c in (n.children or []) if c.name == name]

def contour_info(node):
    if node is None or not isinstance(node.value, (bytes,bytearray)): return None
    d = decode_contour_blob(bytes(node.value))
    segs=d['segments']
    xs=[]; ys=[]
    for s in segs:
        if s['type']=='line': pts=[(s['x1'],s['y1']),(s['x2'],s['y2'])]
        else: pts=[(s['x1'],s['y1']),(s['x2'],s['y2']),(s['x3'],s['y3'])]
        for x,y in pts: xs.append(x);ys.append(y)
    return {'count':len(segs),'types':[s['type'] for s in segs], 'bbox':[min(xs),min(ys),max(xs),max(ys)] if xs else None, 'segments':segs}

def walk_obj(n, parent=''):
    for c in n.children or []:
        if c.name=='Obj':
            f=fields(c); typ=scalar(f,'Type'); ident=scalar(f,'ID'); name=scalar(f,'Name')
            if typ==4002:
                trans=fields(f['Trans']) if 'Trans' in f else {}
                out={'id':ident,'name':name,'thick':scalar(f,'Thick','Thickness'),'trans':{k:v.value for k,v in trans.items()},'contour':contour_info(f.get('Contour')),'cuts':[]}
                cuts=f.get('Cuts')
                if cuts:
                    for cut in children_named(cuts,'Cut'):
                        cf=fields(cut); params=fields(cf['Params']) if 'Params' in cf else {}
                        out['cuts'].append({'name':scalar(cf,'Name'),'front':scalar(cf,'Front'),'params':{k:v.value for k,v in params.items()},'contour':contour_info(cf.get('Contour')),'trajectory':contour_info(cf.get('Trajectory'))})
                print(json.dumps(out, ensure_ascii=False))
            walk_obj(c, parent+'/'+str(name or ident))
        else:
            walk_obj(c,parent)

def main():
    model,meta=parse_current_model(Path(sys.argv[1])); print('META',json.dumps(meta,ensure_ascii=False)); walk_obj(model)
if __name__=='__main__': main()
