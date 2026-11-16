# -*- coding: utf-8 -*-
"""恢复脚本：为全部页面重新挂载返回顶部按钮与完整站点脚本（清理事故的恢复）。"""
import os

BACK_TOP = '''
<button class="back-top" aria-label="返回顶部">
  <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M8 13V3M4 7l4-4 4 4"/></svg>
</button>'''

SCRIPT = '''<script>
(function(){
  // Nav menu
  const burger=document.querySelector('.burger');
  const links=document.querySelector('.nav-links');
  if(burger&&links){
    burger.addEventListener('click',()=>{
      const open=document.body.classList.toggle('menu-open');
      burger.setAttribute('aria-expanded',open?'true':'false');
    });
    links.querySelectorAll('a').forEach(a=>{
      a.addEventListener('click',()=>{
        document.body.classList.remove('menu-open');
        burger.setAttribute('aria-expanded','false');
      });
    });
  }

  // Scroll reveal
  const els=document.querySelectorAll('.reveal');
  if(els.length && 'IntersectionObserver' in window){
    const io=new IntersectionObserver((entries)=>{
      entries.forEach(e=>{
        if(e.isIntersecting){ e.target.classList.add('in'); io.unobserve(e.target); }
      });
    },{threshold:.1});
    els.forEach(el=>io.observe(el));
  }else{ els.forEach(el=>el.classList.add('in')); }
  window.addEventListener('load',()=>{ setTimeout(()=>els.forEach(el=>el.classList.add('in')),300); });

  // Nav tri-state（滚动三态）
  const nav=document.querySelector('nav');
  if(nav){
    const applyNav=()=>{
      const y=window.scrollY;
      nav.classList.toggle('m-past',y>680);
      nav.classList.toggle('m-mid',y>10&&y<=680);
    };
    applyNav();
    window.addEventListener('scroll',applyNav,{passive:true});
  }

  // Marquee（无限滚动）
  const track=document.querySelector('.marquee-track');
  if(track){
    const items=track.children;
    if(items.length){
      track.innerHTML+=track.innerHTML;
      let pos=0;
      const speed=20/60;
      const step=()=>{
        pos-=speed;
        const half=track.scrollWidth/2;
        if(-pos>=half) pos+=half;
        track.style.transform=`translateX(${pos}px)`;
        requestAnimationFrame(step);
      };
      requestAnimationFrame(step);
    }
  }

  // Top banner dismiss
  const banner=document.querySelector('.top-banner');
  if(banner){
    try{ if(localStorage.getItem('yama-banner')==='off'){ banner.remove(); document.body.classList.remove('has-banner'); } }catch(e){}
    const d=banner.querySelector('.dismiss');
    if(d) d.addEventListener('click',()=>{
      banner.remove(); document.body.classList.remove('has-banner');
      try{ localStorage.setItem('yama-banner','off'); }catch(e){}
    });
  }

  // Back to top
  const backTop=document.querySelector('.back-top');
  if(backTop){
    const toggleBack=()=>backTop.classList.toggle('show',window.scrollY>700);
    toggleBack();
    window.addEventListener('scroll',toggleBack,{passive:true});
    backTop.addEventListener('click',()=>window.scrollTo({top:0,behavior:'smooth'}));
  }
})();
</script>'''

count = 0
for f in sorted(x for x in os.listdir('.') if x.endswith('.html') and x != '404.html'):
    s = open(f, encoding='utf-8').read()
    changed = False
    # 返回顶部按钮（放在 </footer> 之后）
    if 'class="back-top"' not in s:
        assert '</footer>' in s, f
        s = s.replace('</footer>', '</footer>\n' + BACK_TOP, 1)
        changed = True
    # 完整站点脚本（放在 </body> 之前）
    if '<script>\n(function(){' not in s:
        assert '</body>' in s, f
        s = s.replace('</body>', SCRIPT + '\n</body>', 1)
        changed = True
    if changed:
        open(f, 'w', encoding='utf-8').write(s)
        count += 1
        print('restored:', f)
print('restored pages:', count)
