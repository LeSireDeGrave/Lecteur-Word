//! Local protocol consumer probe only. No OpusNova source or .nova writer.
use serde_json::Value;
use std::{collections::HashSet,fs,process};
fn check(v:&Value)->Result<(), &'static str>{
    if v["schemaVersion"]!=1 || v["importReady"]!=false || v["pagination"]!="notTested" {return Err("UNSUPPORTED_OR_UNQUALIFIED_CONTRACT");}
    let nodes=v["nodes"].as_array().ok_or("NODES_MISSING")?;
    if nodes.len()>150_000{return Err("NODE_LIMIT");}
    let mut ids=HashSet::new();
    for n in nodes {
        let id=n["id"].as_str().ok_or("ID_MISSING")?;
        if id.len()>11 || !id.starts_with('n') || !id[1..].bytes().all(|c|c.is_ascii_digit()) || !ids.insert(id){return Err("INVALID_NODE_ID");}
        if !n["source"]["partUri"].as_str().is_some_and(|s|s.starts_with('/')&&s.len()<=513){return Err("INVALID_PROVENANCE");}
    }
    for n in nodes {
        if let Some(p)=n["parentId"].as_str(){if !ids.contains(p){return Err("MISSING_PARENT");}}
    }
    for media in v["media"].as_array().ok_or("MEDIA_MISSING")? {
        let id=media["id"].as_str().ok_or("ID_MISSING")?;
        let hash=media["sha256"].as_str().ok_or("HASH_MISSING")?;
        if hash.len()!=64 || !hash.bytes().all(|c|c.is_ascii_hexdigit()&&!c.is_ascii_uppercase()) || id!=format!("sha256-{hash}"){return Err("INVALID_ASSET_ID");}
    }
    Ok(())
}
fn main(){
    let Some(path)=std::env::args().nth(1) else{process::exit(64)};
    let Ok(meta)=fs::metadata(&path) else{process::exit(74)};
    if meta.len()>128*1024*1024{process::exit(75)}
    let result=fs::read(&path).ok().and_then(|b|serde_json::from_slice::<Value>(&b).ok());
    match result.as_ref().map(check){Some(Ok(()))=>println!("OK schemaVersion=1; import remains blocked"),_=>{eprintln!("CONTRACT_REFUSED");process::exit(65)}}
}
