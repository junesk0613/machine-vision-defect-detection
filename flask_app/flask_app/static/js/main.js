const socket=io();
socket.on('connect',()=>{const d=document.getElementById('dot-flask'),l=document.getElementById('lbl-flask');if(d){d.classList.remove('dot-off');d.classList.add('dot-ok')}if(l)l.textContent='Socket: 연결됨'});
socket.on('disconnect',()=>{const d=document.getElementById('dot-flask'),l=document.getElementById('lbl-flask');if(d){d.classList.remove('dot-ok');d.classList.add('dot-off')}if(l)l.textContent='Socket: 연결 끊김'});
socket.on('sensor_update',d=>{const e=id=>document.getElementById(id);if(e('val-temp'))e('val-temp').innerHTML=`${d.temperature}<span class="metric-unit">°C</span>`;if(e('val-humid'))e('val-humid').innerHTML=`${d.humidity}<span class="metric-unit">%</span>`;if(e('val-rpm'))e('val-rpm').innerHTML=`${d.conveyor_rpm}<span class="metric-unit"> RPM</span>`;if(e('bar-ng')){e('bar-ng').style.width=d.box_ng_level+'%';e('pct-ng').textContent=d.box_ng_level+'%'}if(e('bar-ok')){e('bar-ok').style.width=d.box_ok_level+'%';e('pct-ok').textContent=d.box_ok_level+'%'}});
function loadImgWithRetry(el,src,tries,delay){const img=new Image();img.onload=()=>{el.innerHTML=`<img src="${src}" style="width:100%;height:100%;object-fit:cover">`;};img.onerror=()=>{if(tries>0)setTimeout(()=>loadImgWithRetry(el,src,tries-1,delay+500),delay);};img.src=src+'?t='+Date.now();}
function applyInspectionResult(d){const e=id=>document.getElementById(id);if(e('recent-result'))e('recent-result').innerHTML=d.result==='OK'?'<span class="badge-ok">✓ 양품</span>':`<span class="badge-ng">✗ ${d.defect_type||'불량'}</span>`;if(e('recent-time'))e('recent-time').textContent=d.timestamp||'';if(e('recent-front')&&d.front_image)loadImgWithRetry(e('recent-front'),`/images/${d.front_image}`,6,400);if(e('recent-back')&&d.back_image)loadImgWithRetry(e('recent-back'),`/images/${d.back_image}`,6,400);}
socket.on('inspection_result',d=>{applyInspectionResult(d)});
fetch('/api/latest-inspection').then(r=>r.json()).then(d=>{if(d&&d.result)applyInspectionResult(d)}).catch(()=>{});
function refreshAlarmBadge(){fetch('/api/alarm?all=0').then(r=>r.json()).then(data=>{const b=document.getElementById('alarm-badge');if(!b)return;const cnt=Array.isArray(data)?data.filter(a=>!a.resolved).length:0;if(cnt>0){b.textContent=cnt;b.style.display='inline'}else b.style.display='none'}).catch(()=>{})}
refreshAlarmBadge();setInterval(refreshAlarmBadge,30000);
socket.on('alarm_created',()=>{const b=document.getElementById('alarm-badge');if(b){const c=parseInt(b.textContent)||0;b.textContent=c+1;b.style.display='inline'}});
socket.on('alarm_resolved',()=>{const b=document.getElementById('alarm-badge');if(b){const c=parseInt(b.textContent)||0;if(c<=1){b.style.display='none';b.textContent='0'}else b.textContent=c-1}});
socket.on('system_action',d=>{const e=document.getElementById('system-status'),b=document.getElementById('estop-banner');if(!e)return;if(d.action==='start'){e.textContent='가동 중';e.className='status-badge status-running';if(b)b.classList.remove('show')}else if(d.action==='estop'){e.textContent='비상정지';e.className='status-badge status-stopped';if(b)b.classList.add('show')}else{e.textContent='정지';e.className='status-badge status-stopped'}});

// 메시지 미읽음 배지
function refreshMsgBadge(){
  const b=document.getElementById('msg-badge');if(!b)return;
  fetch('/api/chat/lines').then(r=>r.json()).then(lines=>{
    Promise.all((lines||[]).map(l=>fetch(`/api/chat/${l.id}/unread?direction=wpf_to_web`).then(r=>r.json()).then(d=>d.unread||0)))
      .then(arr=>{const total=arr.reduce((a,b)=>a+b,0);if(total>0){b.textContent=total;b.style.display='inline'}else b.style.display='none'});
  }).catch(()=>{});
}
socket.on('chat_message',m=>{
  if(m.direction==='wpf_to_web'&&window.location.pathname!=='/messages'){refreshMsgBadge()}
});
refreshMsgBadge();setInterval(refreshMsgBadge,30000);

function sendRemote(action){if(!confirm((action==='estop'?'비상정지':'라인 '+action)+' 명령을 전송하시겠습니까?'))return;fetch('/api/system',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:action,source:'web'})}).then(r=>r.json()).then(d=>{if(d.ok)console.log('원격 명령 전송:',action)}).catch(e=>alert('명령 전송 실패'))}
