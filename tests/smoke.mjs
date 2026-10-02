import {spawn} from 'node:child_process';
import assert from 'node:assert/strict';
const server=spawn('dotnet',['bin/Debug/net10.0/MonitorRemote.dll'],{env:{...process.env,MONITOR_PORT:'18765',MONITOR_DEMO:'1'},stdio:['ignore','pipe','pipe']});
let logs='';server.stdout.on('data',d=>logs+=d);server.stderr.on('data',d=>logs+=d);
const base='http://127.0.0.1:18765';
async function request(path,data,cookie,origin=base){return fetch(base+path,{method:data===undefined?'GET':'POST',headers:{...(data===undefined?{}:{'Content-Type':'application/json','X-Monitor-Request':'1',Origin:origin}),...(cookie?{Cookie:cookie}:{})},body:data===undefined?undefined:JSON.stringify(data)});}
try{
  let ready=false;for(let i=0;i<100;i++){try{if((await fetch(base)).ok){ready=true;break;}}catch{}await new Promise(r=>setTimeout(r,100));}assert(ready,logs);
  assert.equal((await request('/api/monitors')).status,401);
  const code=logs.match(/Pairing code: (\d{6})/)[1];
  assert.equal((await request('/api/pair',{code},undefined,'http://evil.example')).status,403);
  assert.equal((await request('/api/pair',{code:'wrong'})).status,401);
  const paired=await request('/api/pair',{code});assert.equal(paired.status,200);const cookie=paired.headers.get('set-cookie').split(';')[0];
  assert.equal((await (await request('/api/monitors',undefined,cookie)).json())[0].name,'Demo monitor');
  assert.equal((await request('/api/monitors/0/brightness',{value:101},cookie)).status,400);
  const brightness=await request('/api/monitors/0/brightness',{value:25},cookie);
  assert.equal(brightness.status,200);
  assert.deepEqual(await brightness.json(),{requested:25,confirmed:false,brightness:null,simulated:true});
  assert.equal((await request('/api/displays/power',{state:'invalid'},cookie)).status,400);
  for(const state of ['off','wake']){
    const power=await request('/api/displays/power',{state},cookie);assert.equal(power.status,200);
    assert.deepEqual(await power.json(),{requested:true,confirmed:false,simulated:true});
  }
  assert.equal((await request('/api/logout',{},cookie)).status,200);
  assert.equal((await request('/api/monitors',undefined,cookie)).status,401);
  console.log('PASS: UI, pairing, authorization, origin protection, validation, demo worker and logout');
}finally{server.kill();}
