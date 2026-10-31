import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const tick = async () => { for(let i=0;i<30;i++) await Promise.resolve(); };
class Element {
  constructor(tag='div'){this.tagName=tag;this.children=[];this.dataset={};this.listeners={};this.hidden=false;this.disabled=false;this.value='';this.textContent='';this.attrs={};this.width=300;this.height=200;}
  append(...children){for(const child of children){child.parent=this;this.children.push(child);}}
  remove(){if(this.parent)this.parent.children=this.parent.children.filter(c=>c!==this);}
  replaceChildren(){this.children=[];}
  setAttribute(key,value){this.attrs[key]=value;}
  addEventListener(name,fn){this.listeners[name]=fn;}
  setPointerCapture(){}
  getBoundingClientRect(){return {left:0,top:0,width:300,height:200};}
  getContext(){return new Proxy({}, {get:()=>()=>{}});}
}
function setup(script){
  const html=readFileSync(new URL('../wwwroot/index.html',import.meta.url),'utf8');
  const elements=Object.fromEntries([...html.matchAll(/id="([^"]+)"/g)].map(m=>[m[1],new Element()]));
  for(const m of html.matchAll(/<button[^>]*id="([^"]+)"[^>]*data-remote-input/g)) elements[m[1]].attrs["data-remote-input"]="";
  const walk=e=>[e,...e.children.flatMap(walk)], all=()=>Object.values(elements).flatMap(walk);
  const document={hidden:false,body:{dataset:{}},listeners:{},getElementById:id=>elements[id]||all().find(e=>e.id===id),createElement:tag=>new Element(tag),querySelectorAll:()=>all().filter(e=>Object.hasOwn(e.attrs,'data-remote-input')),addEventListener(name,fn){this.listeners[name]=fn;}};
  let timerId=0, now=1000000; const timers=new Map(), calls=[], sockets=[], active=[];
  const app={connected:true,api(path,data){return new Promise((resolve,reject)=>calls.push({path,data,reply:resolve,fail:reject}));}};
  const window={monitorApp:app,monitorFeatures:{active:value=>active.push(value)},devicePixelRatio:1,listeners:{},addEventListener(name,fn){this.listeners[name]=fn;}};
  class Socket {
    static OPEN=1;
    constructor(url){this.url=String(url);this.readyState=0;this.bufferedAmount=0;this.sent=[];this.autoAck=true;sockets.push(this);}
    open(){this.readyState=1;this.onopen();}
    send(text){const message=JSON.parse(text);this.sent.push(message);if(message.sequence&&this.autoAck)queueMicrotask(()=>this.onmessage?.({data:JSON.stringify({type:'ack',sequence:message.sequence,inserted:true})}));}
    receive(data){this.onmessage({data:typeof data==='object'&&!(data instanceof ArrayBuffer)?JSON.stringify(data):data});}
    close(){this.readyState=3;}
  }
  const context=vm.createContext({document,window,console,URL,location:{href:'http://127.0.0.1:8765/',protocol:'http:'},WebSocket:Socket,Blob,TextDecoder,DataView,Date:class extends Date{static now(){return now;}},
    createImageBitmap:async()=>({width:1280,height:720,close(){}}),setTimeout(fn,ms){timers.set(++timerId,{fn,ms});return timerId;},clearTimeout:id=>timers.delete(id),setInterval(fn,ms){timers.set(++timerId,{fn,ms,interval:true});return timerId;},clearInterval:id=>timers.delete(id)});
  vm.runInContext(readFileSync(new URL('../wwwroot/'+script,import.meta.url),'utf8'),context);
  return {elements,document,window,app,calls,sockets,active,advance(ms){now+=ms;},fire(ms){for(const[id,timer]of [...timers])if(timer.ms===ms){if(!timer.interval)timers.delete(id);timer.fn();}}};
}
const device=(id,direction='output',extra={})=>({id,name:id,direction,state:'active',supported:true,default:id==='speakers',communications:false,volume:50,muted:false,...extra});
const audioSnapshot={revision:'r1',simulated:true,devices:[device('speakers'),device('headphones','output',{muted:true}),device('microphone','input'),device('unsupported','output',{supported:false,volume:null,muted:null})]};
const copy=value=>JSON.parse(JSON.stringify(value));
{
  const ui=setup('audio.js');ui.window.audioUI.tab(true);ui.calls[0].reply(copy(audioSnapshot));await tick();
  const slider=ui.document.getElementById('volume-device-headphones'), pc=ui.document.getElementById('volume-pc-speakers');
  assert.equal(ui.document.getElementById('volume-device-unsupported').disabled,true);
  assert.match(ui.document.getElementById('volume-note-device-unsupported').textContent,/unavailable/);
  slider.value=20;slider.listeners.input();ui.fire(180);slider.value=87;slider.listeners.input();ui.fire(180);
  assert.equal(ui.calls.length,2,'one write at a time');assert.equal(ui.calls[1].data.value,20);assert(!Object.hasOwn(ui.calls[1].data,'muted'));
  ui.calls[1].reply({results:[{confirmed:true,device:device('headphones','output',{volume:20,muted:true})}],simulated:true});await tick();
  assert.equal(ui.calls[2].data.value,87,'latest selection follows readback');assert.equal(Number(slider.value),87);
  slider.value=93;slider.listeners.input();ui.calls[2].fail(Error('Outcome unknown'));await tick();ui.fire(180);
  assert.equal(ui.calls.length,3,'failed change discards queued writes');assert.equal(Number(slider.value),20);assert.match(ui.document.getElementById('volume-note-device-headphones').textContent,/Outcome unknown/);
  ui.fire(1000);ui.calls.at(-1).reply(copy(audioSnapshot));await tick();assert.match(ui.document.getElementById('volume-note-device-headphones').textContent,/Outcome unknown/,'polling preserves failure notice');
  pc.value=40;pc.listeners.input();assert.equal(ui.document.getElementById('volume-device-speakers').disabled,true,'duplicate default card cannot race its device card');ui.fire(180);
  assert.equal(ui.calls.at(-1).data.requireDefault,true);ui.app.connected=false;ui.window.audioUI.connection('offline');ui.calls.at(-1).reply({results:[{confirmed:true,device:device('speakers','output',{volume:40})}],simulated:true});await tick();
  const writes=ui.calls.filter(c=>c.data).length;ui.app.connected=true;ui.window.audioUI.connection('connected');ui.calls.at(-1).reply(copy(audioSnapshot));await tick();ui.fire(180);assert.equal(ui.calls.filter(c=>c.data).length,writes,'reconnect does not replay volume');
  ui.elements['audio-all-apply'].onclick();const bulk=ui.calls.at(-1);assert.deepEqual([...bulk.data.deviceIds],['speakers','headphones','unsupported']);
  bulk.reply({simulated:true,results:[{id:'speakers',confirmed:true,device:device('speakers','output',{volume:30})},{id:'headphones',confirmed:true,device:device('headphones','output',{volume:30})},{id:'unsupported',confirmed:false,error:'Unsupported device'}]});await tick();assert.match(ui.elements['audio-all-note'].textContent,/2 of 3/);assert.match(ui.document.getElementById('volume-note-device-unsupported').textContent,/Unsupported/);
  console.log('PASS: audio latest intent, preserved mute, sibling serialization, failure readback, reconnect without replay, unsupported devices and output-only partial results');
}
const displaySnapshot={revision:'layout',displays:[{id:'left',name:'Left display',x:-1920,y:0,width:1920,height:1080}]};
function frame(){const header=new TextEncoder().encode(JSON.stringify({version:1,sequence:1,revision:'layout',width:1280,height:720,nativeWidth:1920,nativeHeight:1080,x:-1920,y:0,cursorVisible:false}));const bytes=new Uint8Array(4+header.length+2);new DataView(bytes.buffer).setInt32(0,header.length,true);bytes.set(header,4);bytes.set([255,216],4+header.length);return bytes.buffer;}
async function remoteStart(ui){ui.window.remoteUI.tab(true);ui.calls.at(-1).reply({capabilities:{allowViewing:true,allowInput:true},snapshot:displaySnapshot});await tick();ui.elements['remote-display'].value='left';ui.elements['remote-quality'].value='balanced';ui.elements['remote-mode'].value='touchpad';ui.elements['remote-start'].onclick();ui.calls.at(-1).reply({remoteSessionId:'s1',display:displaySnapshot.displays[0]});await tick();for(const socket of ui.sockets.slice(-2))socket.open();const frames=ui.sockets.at(-2),control=ui.sockets.at(-1);frames.receive(frame());await tick();control.receive({type:'heartbeat',allowInput:true,controlEnabled:true});return {frames,control};}
{
  // Hardware mouse and keyboard on the live screen
  const ui=setup('remote.js'),{control}=await remoteStart(ui),canvas=ui.elements['remote-canvas'];
  ui.elements['remote-mode'].value='direct';ui.elements['remote-mode'].onchange();await tick();
  const key=(init)=>{let prevented=false;const ev={key:'',ctrlKey:false,altKey:false,shiftKey:false,metaKey:false,isComposing:false,preventDefault(){prevented=true;},...init};canvas.listeners.keydown(ev);return prevented;};
  let n=control.sent.length;assert.equal(key({key:'a'}),true);await tick();assert.deepEqual([control.sent.at(-1).type,control.sent.at(-1).text],['text','a'],'typed letter becomes text');
  key({key:'A',shiftKey:true});await tick();assert.equal(control.sent.at(-1).text,'A','shifted letter keeps its case');
  key({key:'Enter'});await tick();assert.equal(control.sent.at(-1).type,'key');assert.equal(control.sent.at(-1).key,'Enter');
  key({key:'c',ctrlKey:true});await tick();assert.deepEqual(control.sent.at(-1).modifiers,['Ctrl']);assert.equal(control.sent.at(-1).key,'C','Ctrl+C is a shortcut');
  key({key:'F5'});await tick();assert.equal(control.sent.at(-1).key,'F5','function keys are forwarded');
  n=control.sent.length;assert.equal(key({key:'Shift'}),false);assert.equal(key({key:'v',metaKey:true}),false);assert.equal(control.sent.length,n,'modifier-only and Meta combos send nothing');
  canvas.listeners.pointerdown({pointerId:7,pointerType:'mouse',button:2,clientX:150,clientY:100});await tick();
  assert.deepEqual([control.sent.at(-1).type,control.sent.at(-1).button,control.sent.at(-1).down],['button','right',true],'right mouse button presses the right PC button');
  canvas.listeners.pointerup({pointerId:7,pointerType:'mouse',button:2,clientX:150,clientY:100});await tick();assert.equal(control.sent.at(-1).down,false,'and releases it');
  let blocked=false;canvas.listeners.wheel({deltaX:0,deltaY:100,preventDefault(){blocked=true;}});ui.fire(34);await tick();assert.equal(blocked,true);assert.equal(control.sent.at(-1).type,'wheel');assert.equal(control.sent.at(-1).dy,-1,'mouse wheel scrolls the PC');
  console.log('PASS: physical keyboard, mouse buttons and wheel are forwarded to the PC');
}
{
  const ui=setup('remote.js'),{control}=await remoteStart(ui),canvas=ui.elements['remote-canvas'];
  ui.elements['remote-mode'].value='direct';ui.elements['remote-mode'].onchange();await tick();
  let count=control.sent.length;canvas.listeners.pointerdown({pointerId:1,clientX:150,clientY:0});canvas.listeners.pointerup({pointerId:1,clientX:150,clientY:0});assert.equal(control.sent.length,count,'letterbox touch does not click');
  canvas.listeners.pointerdown({pointerId:2,clientX:150,clientY:100});canvas.listeners.pointerup({pointerId:2,clientX:150,clientY:100});await tick();const mapped=control.sent.findLast(m=>m.type==='move');assert.equal(mapped.x,.5);assert.equal(mapped.y,.5);
  const scroll=ui.elements['remote-scroll'];scroll.onpointerdown({pointerId:3,clientX:0,clientY:0});count=control.sent.length;for(let n=1;n<=300;n++)scroll.onpointermove({clientX:0,clientY:n*5});assert.equal(control.sent.length,count,'fast touch events coalesce');ui.fire(34);await tick();assert.equal(control.sent.length,count+1);assert.equal(control.sent.at(-1).dy,-10);
  ui.advance(2100);ui.fire(200);assert.equal(ui.elements['remote-left'].disabled,true,'stale displayed screen disables input');
  console.log('PASS: direct touch letterbox mapping, normalized negative-origin coordinates, bounded scrolling and stale-frame controls');
}
{
  const ui=setup('remote.js'),{control,frames}=await remoteStart(ui);control.autoAck=false;
  ui.elements['remote-text'].value='Hello 👋';ui.elements['remote-send-text'].onclick();assert.equal(control.sent.at(-1).text,'Hello 👋');ui.fire(3000);await tick();assert.equal(ui.active.at(-1),false);assert.equal(frames.readyState,3);assert.equal(ui.elements['remote-text'].value,'');assert.match(ui.elements['remote-note'].textContent,/timed out/);
  const writes=control.sent.filter(m=>m.type==='text').length;ui.window.remoteUI.connection('connected');assert.equal(control.sent.filter(m=>m.type==='text').length,writes,'uncertain text is never replayed');
  console.log('PASS: uncertain text stops sharing, clears composition and never replays after recovery');
}
console.log('PASS: remote/audio frontend checks. Synthetic DOM and transport; no real PC input or audio.');
