// Animated mesh gradient (WebGL1). Falls back to the CSS gradient behind it.
(function () {
  "use strict";

  var VERT = "attribute vec2 p;void main(){gl_Position=vec4(p,0.,1.);}";
  var FRAG = [
    "precision highp float;",
    "uniform vec2 r;uniform float t;uniform vec3 c0;uniform vec3 c1;uniform vec3 c2;uniform vec3 c3;uniform vec3 c4;",
    // 2D simplex noise (Ashima / Ian McEwan, MIT)
    "vec3 m3(vec3 x){return x-floor(x*(1./289.))*289.;}vec2 m2(vec2 x){return x-floor(x*(1./289.))*289.;}",
    "vec3 pm(vec3 x){return m3(((x*34.)+1.)*x);}",
    "float sn(vec2 v){const vec4 C=vec4(.211324865405187,.366025403784439,-.577350269189626,.024390243902439);",
    "vec2 i=floor(v+dot(v,C.yy));vec2 x0=v-i+dot(i,C.xx);vec2 i1=(x0.x>x0.y)?vec2(1.,0.):vec2(0.,1.);",
    "vec4 x12=x0.xyxy+C.xxzz;x12.xy-=i1;i=m2(i);",
    "vec3 p=pm(pm(i.y+vec3(0.,i1.y,1.))+i.x+vec3(0.,i1.x,1.));",
    "vec3 m=max(.5-vec3(dot(x0,x0),dot(x12.xy,x12.xy),dot(x12.zw,x12.zw)),0.);m=m*m;m=m*m;",
    "vec3 x=2.*fract(p*C.www)-1.;vec3 h=abs(x)-.5;vec3 ox=floor(x+.5);vec3 a0=x-ox;",
    "m*=1.79284291400159-.85373472095314*(a0*a0+h*h);",
    "vec3 g;g.x=a0.x*x0.x+h.x*x0.y;g.yz=a0.yz*x12.xz+h.yz*x12.yw;return 130.*dot(m,g);}",
    "void main(){vec2 uv=gl_FragCoord.xy/r;vec2 q=vec2(uv.x*r.x/r.y,uv.y)*.9;float s=t*.045;",
    "vec2 w=vec2(sn(q*.8+vec2(s,-s*.7)),sn(q*.8+vec2(-s*.6,s)+7.3));q+=w*.35;",
    "float n1=sn(q*1.1+vec2(s*1.3,s*.4));float n2=sn(q*1.6+vec2(-s*.9,s*1.1)+3.1);float n3=sn(q*.7+vec2(s*.5,-s*1.2)+11.7);",
    "vec3 col=c0;",
    "col=mix(col,c1,smoothstep(-.25,.75,n1));",
    "col=mix(col,c2,smoothstep(-.1,.85,n2)*.85);",
    "col=mix(col,c3,smoothstep(.05,.9,n3)*.9);",
    "col=mix(col,c4,smoothstep(.35,1.,n1*n2+uv.y*.35)*.7);",
    "float g=fract(sin(dot(gl_FragCoord.xy,vec2(12.9898,78.233)))*43758.5453);col+=(g-.5)/255.*2.;",
    "gl_FragColor=vec4(col,1.);}"
  ].join("\n");

  function hex(h) {
    var n = parseInt(h.slice(1), 16);
    return [(n >> 16 & 255) / 255, (n >> 8 & 255) / 255, (n & 255) / 255];
  }

  function shader(gl, type, src) {
    var s = gl.createShader(type);
    gl.shaderSource(s, src);
    gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) { gl.deleteShader(s); return null; }
    return s;
  }

  function mount(canvas) {
    var gl = canvas.getContext("webgl", { antialias: false, alpha: false, premultipliedAlpha: false, powerPreference: "low-power" });
    if (!gl) return;
    var vs = shader(gl, gl.VERTEX_SHADER, VERT), fs = shader(gl, gl.FRAGMENT_SHADER, FRAG);
    if (!vs || !fs) return;
    var prog = gl.createProgram();
    gl.attachShader(prog, vs); gl.attachShader(prog, fs); gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) return;
    gl.useProgram(prog);

    var buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1]), gl.STATIC_DRAW);
    var loc = gl.getAttribLocation(prog, "p");
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);

    var colors = (canvas.dataset.colors || "#3b82f6,#3fd9ff,#ff8fc8,#ffc56b,#6ea4ff").split(",");
    ["c0", "c1", "c2", "c3", "c4"].forEach(function (name, i) {
      gl.uniform3fv(gl.getUniformLocation(prog, name), hex(colors[i].trim()));
    });
    var uR = gl.getUniformLocation(prog, "r"), uT = gl.getUniformLocation(prog, "t");

    var reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    var visible = true, raf = 0, start = performance.now() - Math.random() * 40000;

    function resize() {
      // Render at reduced resolution: the gradient is soft, and this keeps it cheap.
      var scale = Math.min(window.devicePixelRatio || 1, 1.5) * 0.5;
      var w = Math.max(1, Math.round(canvas.clientWidth * scale));
      var h = Math.max(1, Math.round(canvas.clientHeight * scale));
      if (canvas.width !== w || canvas.height !== h) {
        canvas.width = w; canvas.height = h;
        gl.viewport(0, 0, w, h);
      }
      gl.uniform2f(uR, w, h);
    }

    function draw(now) {
      resize();
      gl.uniform1f(uT, (now - start) / 1000);
      gl.drawArrays(gl.TRIANGLES, 0, 6);
    }

    function loop(now) {
      draw(now);
      raf = visible && !reduce ? requestAnimationFrame(loop) : 0;
    }

    draw(performance.now());
    canvas.classList.add("is-ready");
    if (!reduce) raf = requestAnimationFrame(loop);

    if ("IntersectionObserver" in window) {
      new IntersectionObserver(function (entries) {
        visible = entries[0].isIntersecting;
        if (visible && !raf && !reduce) raf = requestAnimationFrame(loop);
      }).observe(canvas);
    }
    window.addEventListener("resize", function () { if (reduce || !raf) draw(performance.now()); });
  }

  document.querySelectorAll("canvas[data-gradient]").forEach(mount);
})();
