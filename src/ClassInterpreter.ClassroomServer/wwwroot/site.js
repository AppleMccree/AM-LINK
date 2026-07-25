let token='', courseId='', lesson=null, snapshot=null, timer=null;
const $=id=>document.getElementById(id);
const show=id=>$(id).classList.remove('hidden');
const hide=id=>$(id).classList.add('hidden');

async function api(path,options={}) {
  options.headers={...(options.headers||{}),'Content-Type':'application/json',...(token?{Authorization:`Bearer ${token}`}:{})};
  const response=await fetch(path,options);
  if(!response.ok) {
    let message='';
    try { const body=await response.json(); message=body.error||body.detail||body.title||''; } catch { message=await response.text(); }
    throw new Error(response.status===401?'密码、初始化密钥错误，或登录已经失效':message||`服务器返回 ${response.status}`);
  }
  return response.headers.get('content-type')?.includes('json')?response.json():response;
}

async function initialize() {
  try {
    const status=await api('/api/status');
    hide('loading');
    show(status.setupRequired?'setup':'login');
    $('connection').textContent=`● 服务器正常${status.schoolAiConfigured?' · 学校AI已配置':''}`;
  } catch(error) {
    $('loading').innerHTML=`<h1>课堂服务器无法连接</h1><p class="error">${esc(error.message)}</p>`;
    $('connection').textContent='● 服务器异常';
  }
}

async function setupCourse() {
  const name=$('setupCourse').value.trim(), password=$('setupPassword').value, bootstrapKey=$('bootstrapKey').value;
  if(!name||password.length<6||!bootstrapKey) { $('setupError').textContent='请填写课程名称、至少6位教师密码和初始化密钥'; return; }
  try {
    await api('/api/setup/course',{method:'POST',body:JSON.stringify({name,password,bootstrapKey})});
    $('course').value=name; $('password').value=password; hide('setup'); show('login');
    $('loginError').textContent='课程创建成功，请登录并创建第一节课堂。';
  } catch(error) { $('setupError').textContent=error.message; }
}

async function login() {
  try {
    const result=await api('/api/teacher/login',{method:'POST',body:JSON.stringify({courseName:$('course').value,password:$('password').value})});
    token=result.teacherToken; courseId=result.courseId; $('courseTitle').textContent=$('course').value;
    hide('login'); show('home'); await loadLessons();
  } catch(error) { $('loginError').textContent=error.message; }
}

async function loadLessons() {
  window.activeLessons=await api('/api/teacher/lessons');
  $('lessonList').innerHTML=window.activeLessons.map((item,index)=>`<div class="question"><b>${esc(item.name)}</b> · ${item.endedAt?'<span>已结束</span>':`课堂码 <b>${item.code}</b>`} <button onclick="resumeLesson(${index})">${item.endedAt?'查看记录':'打开看板'}</button></div>`).join('')||'<p>还没有课堂记录</p>';
}
function resumeLesson(index){lesson=window.activeLessons[index];openDashboard()}
async function createLesson(){try{lesson=await api('/api/teacher/lessons',{method:'POST',body:JSON.stringify({name:$('lessonName').value||'课堂'})});$('classCode').textContent=lesson.code;show('lessonCreated');await loadLessons()}catch(error){alert(error.message)}}
function openDashboard(){hide('home');show('dashboard');$('lessonTitle').textContent=lesson.name+(lesson.endedAt?' · 历史记录':' · 教师实时看板');$('dashCode').textContent=lesson.endedAt?'已结束':lesson.code;$('endLessonButton').classList.toggle('hidden',Boolean(lesson.endedAt));refresh();clearInterval(timer);if(!lesson.endedAt)timer=setInterval(refresh,2000)}
function backHome(){clearInterval(timer);timer=null;hide('dashboard');show('home');loadLessons()}
async function refresh(){try{snapshot=await api(`/api/teacher/lessons/${lesson.id}/snapshot`);render();$('connection').textContent='● 已连接'}catch{$('connection').textContent='● 重连中'}}

function render(){
  if(!snapshot)return;
  $('online').textContent=snapshot.onlineStudents; $('questions').textContent=snapshot.questionCount; $('askers').textContent=snapshot.anonymousAskers; $('unresolved').textContent=snapshot.unaddressedQuestions; $('confusions').textContent=snapshot.confusionCount;
  const questions=$('onlyOpen').checked?snapshot.questions.filter(item=>!item.isAddressed):snapshot.questions;
  $('questionList').innerHTML=questions.map(item=>`<article class="question ${item.isPinned?'pinned':''}"><b>${esc(item.question)}</b><div class="qmeta">${new Date(item.askedAt).toLocaleTimeString()} · ${item.transcriptTimestamp||'无时间戳'} · ${item.slidePage?'PPT第'+item.slidePage+'页':'无页码'} · 👍 ${item.votes} · ${esc(item.topic)}</div>${item.selectedContext?`<div class="qmeta">上下文：${esc(item.selectedContext)}</div>`:''}<div class="qactions"><button class="ghost" onclick="state('${item.id}','pinned',${!item.isPinned})">${item.isPinned?'取消置顶':'置顶'}</button><button onclick="state('${item.id}','addressed',${!item.isAddressed})">${item.isAddressed?'标记未讲':'已讲解'}</button></div></article>`).join('')||'<p>还没有问题</p>';
  hot('topicHotspots',count(snapshot.questions,item=>item.topic)); hot('pageHotspots',count(snapshot.questions.filter(item=>item.slidePage),item=>'PPT '+item.slidePage)); hot('timeHotspots',count(snapshot.questions.filter(item=>item.transcriptTimestamp),item=>item.transcriptTimestamp));
  $('broadcasts').innerHTML=snapshot.broadcasts.map(item=>`<div class="broadcast">${esc(item.message)} <span>${new Date(item.sentAt).toLocaleTimeString()}</span></div>`).join('');
}
function count(items,key){const map={};items.forEach(item=>map[key(item)]=(map[key(item)]||0)+1);return Object.entries(map).sort((a,b)=>b[1]-a[1]).slice(0,8)}
function hot(id,items){$(id).innerHTML=items.map(([key,value])=>`<span class="chip">${esc(String(key))} × ${value}</span>`).join('')||'<span>暂无</span>'}
async function state(id,field,value){await api(`/api/teacher/lessons/${lesson.id}/questions/${id}/${field}`,{method:'POST',body:JSON.stringify({value})});refresh()}
async function sendBroadcast(){const message=$('broadcast').value;if(!message)return;await api(`/api/teacher/lessons/${lesson.id}/broadcast`,{method:'POST',body:JSON.stringify({message})});$('broadcast').value='';refresh()}
async function downloadCsv(){const response=await fetch(`/api/teacher/lessons/${lesson.id}/export.csv`,{headers:{Authorization:`Bearer ${token}`}});const blob=await response.blob();const link=document.createElement('a');link.href=URL.createObjectURL(blob);link.download='AM-LINK课堂问题.csv';link.click()}
async function endLesson(){if(!confirm('结束后学生不能再加入，但问题数据会保留。确定结束吗？'))return;await api(`/api/teacher/lessons/${lesson.id}/end`,{method:'POST'});alert('课堂已结束');backHome()}
async function deleteLesson(){if(!confirm('确定永久删除本课堂及问题数据？此操作不可恢复。'))return;await api(`/api/teacher/lessons/${lesson.id}`,{method:'DELETE'});backHome()}
async function changePassword(){if(!confirm('修改后，所有老师需要用新密码重新登录。继续吗？'))return;await api('/api/teacher/password',{method:'POST',body:JSON.stringify({newPassword:$('newPassword').value})});alert('密码已修改');location.reload()}
function esc(value){return String(value||'').replace(/[&<>"']/g,char=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]))}

initialize();
