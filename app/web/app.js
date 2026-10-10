"use strict";
(function(){
  // ---- 지도 투영 설정 ----
  var LAT_TOP = 80, LAT_BOT = -58, PAD_X = 28, PAD_T = 22, PAD_B = 30;
  var DEFAULT_ORIGIN = { lon:127.0, lat:37.5 }; // 위치 미확인 시 기준점

  var COL = {
    land:  "rgba(86,150,214,0.00)",
    good:"#37d58f", mid:"#f3b63f", bad:"#ff5d6c", dead:"#53627d",
    cyan:"#36e6ff", sky:"#5bc8ff"
  };

  var stage = document.getElementById("stage");
  var canvas = document.getElementById("map");
  var ctx = canvas.getContext("2d");
  var tooltip = document.getElementById("tooltip");

  var W = 0, H = 0, DPR = 1;
  var base = document.createElement("canvas");
  var bctx = base.getContext("2d");

  var state = {
    mode: "ping",
    me: null,            // {lon,lat,ip,located,city,country,org}
    meKnown: false,
    pings: [],           // {t, res, flash}
    trace: { host:null, hops:[], active:false, es:null, t0:0 }
  };

  function project(lon, lat){
    var fx = (lon + 180) / 360;
    var fy = (LAT_TOP - lat) / (LAT_TOP - LAT_BOT);
    return {
      x: PAD_X + fx * (W - 2*PAD_X),
      y: PAD_T + fy * (H - PAD_T - PAD_B)
    };
  }
  function origin(){
    if (state.me && state.me.located) return { lon:state.me.lon, lat:state.me.lat };
    return DEFAULT_ORIGIN;
  }

  // ---- 리사이즈 + 베이스 지도 ----
  function resize(){
    DPR = Math.max(1, Math.min(2, window.devicePixelRatio || 1));
    W = stage.clientWidth; H = stage.clientHeight;
    canvas.width = Math.round(W*DPR); canvas.height = Math.round(H*DPR);
    ctx.setTransform(DPR,0,0,DPR,0,0);
    base.width = canvas.width; base.height = canvas.height;
    drawBase();
  }

  function drawBase(){
    bctx.setTransform(DPR,0,0,DPR,0,0);
    bctx.clearRect(0,0,W,H);
    var dots = window.WORLD_DOTS || [];
    for (var i=0;i<dots.length;i+=2){
      var lon = dots[i]/10, lat = dots[i+1]/10;
      var p = project(lon, lat);
      // 위도에 따라 아주 미세한 밝기 변화로 깊이감
      var t = (lat+60)/140;
      var a = 0.22 + 0.16*Math.max(0,Math.min(1,t));
      bctx.beginPath();
      bctx.arc(p.x, p.y, 1.1, 0, 6.2832);
      bctx.fillStyle = "rgba(96,158,221,"+a.toFixed(3)+")";
      bctx.fill();
    }
  }

  // ---- 베지어 아크 ----
  function arc(a, b, lift){
    var mx = (a.x+b.x)/2, my=(a.y+b.y)/2;
    var dx = b.x-a.x, dy=b.y-a.y;
    var len = Math.sqrt(dx*dx+dy*dy)||1;
    // 위로 들어올리는 수직 방향
    var nx = -dy/len, ny = dx/len;
    if (ny > 0){ nx=-nx; ny=-ny; } // 항상 위쪽으로
    var k = Math.min(0.32, 0.12 + len/2600) * (lift||1);
    return { ax:a.x, ay:a.y, cx:mx+nx*len*k, cy:my+ny*len*k, bx:b.x, by:b.y };
  }
  function bez(A,t){
    var u=1-t;
    return {
      x: u*u*A.ax + 2*u*t*A.cx + t*t*A.bx,
      y: u*u*A.ay + 2*u*t*A.cy + t*t*A.by
    };
  }
  function strokeArc(A, color, width, alpha){
    ctx.beginPath();
    ctx.moveTo(A.ax,A.ay);
    ctx.quadraticCurveTo(A.cx,A.cy,A.bx,A.by);
    ctx.strokeStyle = color;
    ctx.globalAlpha = alpha==null?1:alpha;
    ctx.lineWidth = width||1.4;
    ctx.stroke();
    ctx.globalAlpha = 1;
  }
  function packet(A, t, color, r){
    var p = bez(A, t);
    ctx.beginPath();
    ctx.arc(p.x,p.y,r||2.3,0,6.2832);
    ctx.fillStyle = color;
    ctx.shadowColor = color; ctx.shadowBlur = 9;
    ctx.fill();
    ctx.shadowBlur = 0;
  }
  function node(p, color, r, glow){
    ctx.beginPath();
    ctx.arc(p.x,p.y,r,0,6.2832);
    ctx.fillStyle = color;
    ctx.shadowColor = color; ctx.shadowBlur = glow||10;
    ctx.fill(); ctx.shadowBlur = 0;
  }
  function ring(p, color, r, a){
    ctx.beginPath(); ctx.arc(p.x,p.y,r,0,6.2832);
    ctx.strokeStyle=color; ctx.globalAlpha=a; ctx.lineWidth=1.4; ctx.stroke(); ctx.globalAlpha=1;
  }

  function colorFor(ms, ok){
    if (!ok) return COL.dead;
    if (ms<=80) return COL.good;
    if (ms<=160) return COL.mid;
    return COL.bad;
  }

  // ---- 메인 렌더 루프 ----
  var hoverNode = null;
  function frame(ts){
    ctx.clearRect(0,0,W,H);
    ctx.drawImage(base, 0,0, W, H);
    var o = project(origin().lon, origin().lat);

    if (state.mode==="ping"){
      for (var i=0;i<state.pings.length;i++){
        var pg = state.pings[i];
        if (!pg.res || !pg.res.located) continue;
        var np = project(pg.res.lon, pg.res.lat);
        var col = colorFor(pg.res.rtt, pg.res.ok);
        var A = arc(o, np, 1);
        strokeArc(A, col, 1.3, pg.res.ok?0.5:0.22);
        if (pg.res.ok){
          var t1 = ((ts/2200) + pg.phase) % 1;
          packet(A, t1, col, 2.2);
        }
        node(np, col, pg===hoverNode?5:3.4, pg.res.ok?12:5);
      }
    } else {
      // 경로 추적
      var located = [{x:o.x,y:o.y}];
      for (var h=0; h<state.trace.hops.length; h++){
        var hp = state.trace.hops[h];
        if (hp.located){ located.push(project(hp.lon, hp.lat)); }
      }
      // 세그먼트
      var arcs = [];
      for (var s=0; s<located.length-1; s++){ arcs.push(arc(located[s], located[s+1], 0.85)); }
      for (var s2=0;s2<arcs.length;s2++) strokeArc(arcs[s2], COL.sky, 1.4, 0.55);
      // 흐르는 패킷(전체 경로를 따라)
      if (arcs.length){
        var seg = ( (ts/1400) ) % arcs.length;
        var si = Math.floor(seg), st = seg - si;
        packet(arcs[si], st, COL.cyan, 2.6);
        if (arcs.length>1){ var si2=(si+1)%arcs.length; packet(arcs[si2],(st+0.5)%1, COL.cyan, 1.8); }
      }
      for (var n2=1;n2<located.length;n2++) node(located[n2], COL.sky, 3.2, 10);
    }

    // 내 위치 마커
    var pulse = 7 + 3*Math.sin(ts/420);
    ring(o, COL.cyan, pulse, 0.5);
    ring(o, COL.cyan, pulse+5, 0.18);
    node(o, COL.cyan, 4, 14);

    requestAnimationFrame(frame);
  }

  // ---- 상단 상태바 ----
  function flag(cc){ return cc && cc.length===2 ? cc : ""; }
  function setStatus(me){
    var loc = document.getElementById("st-loc");
    var ip = document.getElementById("st-ip");
    var isp = document.getElementById("st-isp");
    if (me && me.located){
      loc.textContent = (me.city?me.city+", ":"") + (flag(me.country)||"");
    } else loc.textContent = "미확인";
    ip.textContent = me && me.ip ? me.ip : "미확인";
    isp.textContent = me && me.org ? shortOrg(me.org) : "미확인";
  }
  function shortOrg(s){ s=s.replace(/\s+(Corporation|Corp\.?|Inc\.?|Ltd\.?|Co\.?,?|LLC|Company|Limited)\b.*$/i,""); return s.length>22?s.slice(0,22)+"…":s; }

  // ---- 서버 핑 ----
  function pingRowHtml(t){
    return '<li class="srv-row measuring" id="row-'+t.Id+'">'
      + '<span class="pulse"></span>'
      + '<span class="meta"><span class="name">'+esc(t.Label)+'</span>'
      + '<span class="sub" id="sub-'+t.Id+'">측정 중</span></span>'
      + '<span class="ms" id="ms-'+t.Id+'">···</span></li>';
  }
  function esc(s){ return (s==null?"":String(s)).replace(/[&<>"]/g,function(c){return {"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c];}); }

  function runPings(){
    var list = document.getElementById("srv-list");
    fetch("/api/targets").then(function(r){return r.json();}).then(function(targets){
      state.pings = targets.map(function(t,i){ return {t:t, res:null, phase:i*0.13}; });
      list.innerHTML = targets.map(pingRowHtml).join("");
      targets.forEach(function(t){
        fetch("/api/ping?host="+encodeURIComponent(t.Host)).then(function(r){return r.json();}).then(function(res){
          var pg = state.pings.find(function(x){return x.t.Id===t.Id;});
          if (pg) pg.res = res;
          paintRow(t, res);
        }).catch(function(){ paintRow(t, {ok:false,located:false}); });
      });
    });
  }
  function paintRow(t, res){
    var row = document.getElementById("row-"+t.Id);
    var ms = document.getElementById("ms-"+t.Id);
    var sub = document.getElementById("sub-"+t.Id);
    if (!row) return;
    row.className = "srv-row";
    if (!res.ok){
      row.classList.add("dead"); ms.textContent = "응답 없음";
      sub.textContent = res.located ? place(res) : (res.ip||"");
      return;
    }
    var cls = res.rtt<=80?"good":res.rtt<=160?"mid":"bad";
    row.classList.add(cls);
    ms.innerHTML = res.rtt + '<small>ms</small>';
    sub.textContent = place(res) + (res.loss>0? "  손실 "+res.loss+"%":"");
  }
  function place(r){
    var parts=[];
    if (r.city) parts.push(r.city);
    if (r.country) parts.push(r.country);
    var s = parts.join(", ");
    if (r.org) s += (s?"  ":"") + shortOrg(r.org);
    return s || (r.ip||"");
  }

  // ---- 경로 추적 ----
  function startTrace(host){
    if (!host) return;
    if (state.trace.es){ try{state.trace.es.close();}catch(e){} }
    state.mode = "trace";
    state.trace = { host:host, hops:[], active:true, es:null, t0:performance.now() };
    document.getElementById("hop-list").innerHTML = "";
    document.getElementById("trace-empty").style.display = "none";
    document.getElementById("trace-btn").disabled = true;
    document.getElementById("trace-host").value = host;

    var es = new EventSource("/api/trace?host="+encodeURIComponent(host));
    state.trace.es = es;
    es.onmessage = function(ev){
      var hop; try{ hop = JSON.parse(ev.data); }catch(e){ return; }
      if (!hop || typeof hop.ttl==="undefined") return;
      state.trace.hops.push(hop);
      addHopRow(hop);
    };
    es.addEventListener("end", function(){ finishTrace(); });
    es.onerror = function(){ finishTrace(); };
  }
  function finishTrace(){
    if (state.trace.es){ try{state.trace.es.close();}catch(e){} state.trace.es=null; }
    state.trace.active=false;
    document.getElementById("trace-btn").disabled = false;
  }
  function addHopRow(hop){
    var list = document.getElementById("hop-list");
    var li = document.createElement("li");
    var cls = "hop-row";
    if (hop.located) cls+=" located";
    if (hop.timeout) cls+=" timeout";
    if (hop.done) cls+=" dest";
    li.className = cls;
    var place2, sub2, ms2;
    if (hop.timeout){ place2="응답 없는 구간"; sub2="*"; ms2=""; }
    else if (hop.isPrivate){ place2="내 네트워크"; sub2=hop.ip||""; ms2=hop.rtt>=0?hop.rtt+"ms":""; }
    else if (hop.located){ place2=(hop.city?hop.city+", ":"")+(hop.country||""); sub2=shortOrg(hop.org||"")+(hop.ip?"  "+hop.ip:""); ms2=hop.rtt>=0?hop.rtt+"ms":""; }
    else { place2=hop.ip||"알 수 없음"; sub2=""; ms2=hop.rtt>=0?hop.rtt+"ms":""; }
    li.innerHTML = '<span class="idx">'+(hop.done?"끝":hop.ttl)+'</span>'
      + '<span class="hmeta"><span class="hplace">'+esc(place2)+'</span><span class="hsub">'+esc(sub2)+'</span></span>'
      + '<span class="hms">'+esc(ms2)+'</span>';
    list.appendChild(li);
    list.scrollTop = list.scrollHeight;
  }

  // ---- 탭 ----
  document.querySelectorAll(".tab").forEach(function(btn){
    btn.addEventListener("click", function(){
      document.querySelectorAll(".tab").forEach(function(b){b.classList.remove("active");});
      document.querySelectorAll(".tabpane").forEach(function(p){p.classList.remove("active");});
      btn.classList.add("active");
      var id = btn.getAttribute("data-tab");
      document.getElementById("pane-"+id).classList.add("active");
      state.mode = id;
    });
  });
  document.getElementById("btn-reping").addEventListener("click", runPings);
  document.getElementById("trace-form").addEventListener("submit", function(e){
    e.preventDefault();
    startTrace(document.getElementById("trace-host").value.trim());
  });
  document.querySelectorAll("#trace-quick button").forEach(function(b){
    b.addEventListener("click", function(){
      document.querySelector('.tab[data-tab="trace"]').click();
      startTrace(b.getAttribute("data-host"));
    });
  });

  // ---- 툴팁(호버) ----
  stage.addEventListener("mousemove", function(e){
    var rect = canvas.getBoundingClientRect();
    var mx = e.clientX-rect.left, my=e.clientY-rect.top;
    hoverNode = null; var best=14*14;
    if (state.mode==="ping"){
      for (var i=0;i<state.pings.length;i++){
        var pg=state.pings[i]; if(!pg.res||!pg.res.located) continue;
        var p=project(pg.res.lon,pg.res.lat);
        var d=(p.x-mx)*(p.x-mx)+(p.y-my)*(p.y-my);
        if(d<best){best=d;hoverNode=pg;}
      }
    }
    if (hoverNode){
      var r=hoverNode.res, pp=project(r.lon,r.lat);
      tooltip.innerHTML='<div class="tt-name">'+esc(hoverNode.t.Label)+' '+(r.ok?r.rtt+"ms":"응답 없음")+'</div>'
        +'<div class="tt-sub">'+esc(place(r))+'</div>';
      tooltip.style.left=pp.x+"px"; tooltip.style.top=pp.y+"px";
      tooltip.classList.remove("hidden");
    } else tooltip.classList.add("hidden");
  });
  stage.addEventListener("mouseleave", function(){ tooltip.classList.add("hidden"); });

  // ---- 하트비트(창 닫으면 프로그램 종료) ----
  function beat(){ fetch("/api/alive").catch(function(){}); }
  setInterval(beat, 3000); beat();

  // ---- 시작 ----
  function init(){
    resize();
    window.addEventListener("resize", resize);
    requestAnimationFrame(frame);
    fetch("/api/me").then(function(r){return r.json();}).then(function(me){
      state.me = me; state.meKnown = !!(me&&me.located);
      setStatus(me);
    }).catch(function(){ setStatus(null); });
    runPings();
  }
  init();
})();
