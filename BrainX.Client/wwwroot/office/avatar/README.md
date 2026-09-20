# BrainX Avatar Game Pack — 1.0

ชุดอวาตาร์สำหรับห้อง Cowork บน Canvas 2D: **40 แอนิเมชัน / 187 เฟรม PNG / 256 × 256 พิกเซล** พื้นหลังโปร่งใสและจุดยึดเดียวกัน (128, 240)

## เปิดดูทันที

เปิด `preview.html` ด้วยเบราว์เซอร์ได้โดยตรง ไฟล์นี้ฝังภาพและข้อมูลทั้งหมดไว้แล้ว ไม่ต้องติดตั้งหรือเปิดเซิร์ฟเวอร์

- เลือกท่าจากการ์ดหรือค้นหาชื่อท่า
- หยุด / เล่นต่อ / เลื่อนดูแต่ละจังหวะ / ปรับความเร็ว
- ปิด “เล่นซ้ำเพื่อดูท่า” เพื่อทดลองท่าที่เล่นครั้งเดียวแล้วกลับพัก
- เปิด “ฉากห้อง” แล้วคลิกบริเวณพื้นเพื่อทดลองเดิน
- ปุ่มลำดับตัวอย่าง: สั่งงาน → นั่ง → พิมพ์ → ลุก → ดีใจ → พัก

`index.html` เป็นพรีวิวแบบแยกไฟล์ ใช้เมื่อเสิร์ฟผ่าน HTTP หรือ WebView2 virtual host

## ไฟล์ที่ใช้ในเกม

| ไฟล์ | ใช้ทำอะไร |
|---|---|
| brainx-avatar.js | ตัวเล่น Canvas 2D แบบ ES module ไม่มี dependency |
| animations.json | รายการท่า จังหวะ จุดยึด พิกัดภาพ และเหตุการณ์ |
| atlas-0.png ถึง atlas-2.png | ภาพรวมเฟรมสำหรับโหลดเข้าเกมเพียง 3 ภาพ |
| frames/ | PNG แยก 187 เฟรม |
| strips/ | แผ่นภาพแนวนอนแยกตามท่า 40 ไฟล์ |
| preview.html | พรีวิวแบบไฟล์เดียว เปิดออฟไลน์ได้ |
| catalog.png | ภาพรวมทุกท่า |
| sources/ และ prompts.json | ภาพต้นทางและคำสั่ง imagegen สำหรับแก้ไขต่อ |
| validation.json / runtime-tests.json | ผลตรวจไฟล์ภาพและตัวเล่น |

## เชื่อมกับ Canvas ของ BrainX

วาง `brainx-avatar.js`, `animations.json` และ `atlas-*.png` ไว้ในโฟลเดอร์เดียวกันภายใน web assets ของ BrainX จากนั้นใช้แบบนี้:

```js
import { BrainXAvatar } from './avatar/brainx-avatar.js';

const avatar = await BrainXAvatar.load('./avatar/', {
  x: 480, y: 400, scale: 0.65
});

let previous = 0;
function tick(now) {
  const dt = previous ? (now - previous) / 1000 : 0;
  previous = now;

  // วาดฉากและเฟอร์นิเจอร์ด้านหลังก่อน
  // clear / drawBackground ของเกมคุณ
  avatar.update(dt);   // dt เป็นวินาที
  avatar.draw(ctx);    // x,y คือจุดยึดที่พื้นใต้เท้า

  // วาดเฟอร์นิเจอร์ที่บังตัวละครด้านหน้า หลังจากนี้
  requestAnimationFrame(tick);
}
requestAnimationFrame(tick);
```

ตัวอย่างคำสั่ง:
```js
avatar.play('angry');                 // โมโห วนซ้ำตามค่าเริ่มต้น
avatar.play('scold', {loop: false});   // ต่อว่า แล้วกลับ idle
avatar.play('love', {loop: false});    // ส่งหัวใจ
avatar.play('instruct', {loop: false});
avatar.play('sad');
avatar.play('joy', {loop: false});
avatar.moveTo(620, 430, {speed: 65});  // เลือกทิศเดินให้อัตโนมัติ
avatar.stop();                        // หยุดและพักตามทิศ/ท่านั่ง
avatar.paused = true;
avatar.rate = 0.75;

avatar.play('sit_down', {loop: false})
  .enqueue('typing', {loop: false})
  .enqueue('stand_up', {loop: false})
  .enqueue('celebrate', {loop: false});

avatar.onEvent = event => {
  if (event.name === 'footstep') {
    // event.foot = left / right
    // เชื่อมเสียงฝีเท้าที่เกมจัดการเอง
  }
};
avatar.onComplete = name => { /* เมื่อจบรอบหรือจบท่า */ };
```

เมื่อ BrainX ส่งสถานะซ้ำ อย่าเริ่มแอนิเมชันใหม่ทุกครั้ง:
```js
avatar.play('typing', {restart: false});
```

หากมีตัวละครหลายตัว ให้แชร์ `data` และ `images` เพื่อลดการโหลดซ้ำ:
```js
const second = new BrainXAvatar(avatar.data, avatar.images, {
  x: 320, y: 400, scale: 0.65
});
```

## รายการท่า

- เดิน: `walk_se`, `walk_sw`, `walk_ne`, `walk_nw`
- วิ่ง: `run` (มุมหน้าขวา)
- พื้นฐาน: `idle`, `idle_sw`, `idle_ne`, `idle_nw`, `sit_down`, `stand_up`, `seated_idle`
- งาน: `typing`, `think`, `read`, `write`
- สื่อสาร: `talk`, `instruct`, `point`, `listen`
- อารมณ์บวก: `joy`, `celebrate`, `laugh`, `love`
- อารมณ์ลบ: `sad`, `cry`, `angry`, `scold`
- ปฏิสัมพันธ์: `wave`, `agree`, `disagree`, `shy`
- ปฏิกิริยา: `confused`, `surprised`, `worried`, `frustrated`
- พัก: `tired`, `sleep`, `drink`, `phone`

## ขอบเขตและการจัดวาง

เดินมี 4 ทิศเฉียงและ 12 เฟรมต่อรอบ วิ่งมี 12 เฟรม ท่างานและอารมณ์ใช้มุมหน้าสามส่วนตามคอนเซปต์ ส่วนใหญ่มีภาพท่าหลัก 4 เฟรมพร้อมจังหวะเล่นที่กำหนดไว้ ท่าพักด้านหลังและท่านั่งพักใช้ภาพค้าง สถานะเหล่านี้แยกจากแอนิเมชันที่ขยับ

พิกัด `x,y` อยู่ที่พื้นใต้ตัวละคร ไม่ใช่มุมซ้ายบน การนั่งแยกเก้าอี้/โต๊ะออกจากภาพ ให้เกมวางเก้าอี้และโต๊ะตามฉากและเรียงลำดับการบังตามความลึก ตำแหน่งมืออาจต้องจัดให้พอดีกับโต๊ะของเกม

`moveTo` เป็นการเดินตรงไปยังจุดหมาย ไม่ใช่ระบบหลบสิ่งกีดขวาง เกมควรส่ง waypoint ที่เดินได้จริงเข้ามา ภาพห้องในพรีวิวใช้เป็นฉากสาธิต

แพ็กนี้ทดสอบกับตัวเล่น Canvas ที่ให้มาด้วย ยังไม่ได้แก้หรือติดตั้งลงในโค้ดโปรเจกต์ BrainX จริง

## การผลิตและตรวจสอบ

ภาพท่าทางและภาพต้นแบบ 4 มุมสร้างด้วย built-in image_gen จากอวาตาร์เดิม จากนั้นตัดเฟรม จัดจุดยึด ทำความสะอาด alpha และประกอบวงจรขาแบบ cutout ให้ก้าวสลับกัน ก่อนส่งออกเป็น PNG ที่เกมเล่นได้โดยไม่ต้องมีระบบ rig

ตรวจความครบถ้วนของภาพ พิกัด alpha ขอบภาพ จังหวะ และการสลับขาแล้ว ทดสอบทั้ง 40 คลิป การต่อคิว การกลับท่าพัก การเดินครบ 4 ทิศ และ pause/resume รวมถึงเปิดพรีวิวและทดสอบการคลิกในเบราว์เซอร์

