using System.Globalization;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// Thai language helpers — grapheme-aware word counting and a Thai
/// keyword dictionary for category scoring.
///
/// Thai writes without spaces between words, so a plain Split(' ')
/// undercounts heavily: "ประโยคภาษาไทยยาวๆ" becomes 1 "word". We
/// approximate by counting text elements (graphemes) when the content
/// is mostly Thai, falling back to whitespace tokenization otherwise.
///
/// The keyword dictionary lets Thai notes land in the right knowledge
/// category instead of everything falling through to "Other".
/// </summary>
public static class ThaiTextSupport
{
    /// <summary>
    /// Thai keywords per category. Mirrors the English dict in
    /// KnowledgeIndexer — any note that contains these terms in Thai
    /// gets the same category credit an English note would.
    /// </summary>
    public static readonly Dictionary<KnowledgeCategory, string[]> ThaiCategoryKeywords = new()
    {
        [KnowledgeCategory.Programming] = [
            "โค้ด", "ฟังก์ชัน", "คลาส", "อัลกอริทึม", "ตัวแปร", "ลูป", "อาเรย์",
            "เอพีไอ", "ดีบัก", "คอมไพเลอร์", "ไวยากรณ์", "คอมมิท", "รีแฟคเตอร์",
            "เขียนโปรแกรม", "โปรแกรมเมอร์", "ซอร์สโค้ด", "ไลบรารี", "ภาษาโปรแกรม",
        ],
        [KnowledgeCategory.AI_MachineLearning] = [
            "ปัญญาประดิษฐ์", "เอไอ", "แมชชีนเลิร์นนิง", "นิวรัลเน็ตเวิร์ก", "ดีปเลิร์นนิง",
            "โมเดลภาษา", "แอลแอลเอ็ม", "ทรานส์ฟอร์เมอร์", "เทรนโมเดล", "พรอมต์",
            "เรียนรู้ของเครื่อง", "โครงข่ายประสาท", "การเรียนรู้เชิงลึก",
        ],
        [KnowledgeCategory.Blockchain_Web3] = [
            "บล็อกเชน", "สัญญาอัจฉริยะ", "โทเค็น", "กระเป๋า", "คริปโต", "บิตคอยน์",
            "อีเธอเรียม", "ดีไฟ", "เอ็นเอฟที", "แฮช", "กระจายศูนย์", "เว็บสาม",
        ],
        [KnowledgeCategory.Science] = [
            "ทดลอง", "สมมติฐาน", "ทฤษฎี", "งานวิจัย", "ฟิสิกส์", "เคมี", "ชีววิทยา",
            "ควอนตัม", "โมเลกุล", "อะตอม", "พลังงาน", "แรงโน้มถ่วง", "วิทยาศาสตร์",
        ],
        [KnowledgeCategory.Mathematics] = [
            "สมการ", "ทฤษฎีบท", "พิสูจน์", "แคลคูลัส", "พีชคณิต", "เรขาคณิต",
            "สถิติ", "ความน่าจะเป็น", "เมทริกซ์", "อินทิกรัล", "อนุพันธ์", "คณิตศาสตร์",
        ],
        [KnowledgeCategory.Engineering] = [
            "ระบบ", "ออกแบบ", "สถาปัตยกรรม", "วงจร", "เครื่องกล", "ไฟฟ้า",
            "โครงสร้าง", "ซีเอดี", "จำลอง", "ต้นแบบ", "การผลิต", "วิศวกรรม",
        ],
        [KnowledgeCategory.Design_Art] = [
            "ดีไซน์", "สี", "ตัวอักษร", "เลย์เอาต์", "ยูไอ", "ยูเอ็กซ์", "ภาพประกอบ",
            "กราฟิก", "สุนทรียะ", "องค์ประกอบ", "พาเลตต์", "ฟิกมา", "ศิลปะ", "ออกแบบ",
        ],
        [KnowledgeCategory.Business_Finance] = [
            "ตลาด", "รายได้", "กลยุทธ์", "ลงทุน", "ผลตอบแทน", "สตาร์ทอัพ", "หุ้น",
            "มูลค่า", "กำไร", "เติบโต", "ลูกค้า", "ผลิตภัณฑ์", "ธุรกิจ", "การเงิน",
        ],
        [KnowledgeCategory.Security_Crypto] = [
            "ความปลอดภัย", "เข้ารหัส", "ช่องโหว่", "เอ็กซ์พลอยต์", "ไฟร์วอลล์",
            "ยืนยันตัวตน", "สิทธิ์", "เพนเทส", "มัลแวร์", "แฮก", "ไซเบอร์",
            "ไพรเวตคีย์", "พับลิกคีย์", "ลายเซ็น",
        ],
        [KnowledgeCategory.DevOps_Cloud] = [
            "ด็อกเกอร์", "คูเบอร์เนตส์", "ซีไอ", "ซีดี", "ไปป์ไลน์", "คลาวด์",
            "เอดับเบิลยูเอส", "ดีพลอย", "คอนเทนเนอร์", "ไมโครเซอร์วิส", "เซิร์ฟเวอร์ไร้",
        ],
        [KnowledgeCategory.Web_Development] = [
            "เอชทีเอ็มแอล", "ซีเอสเอส", "จาวาสคริปต์", "รีแอค", "วิว", "แองกูลาร์",
            "ฟรอนต์เอนด์", "แบ็กเอนด์", "เรสต์", "กราฟคิวแอล", "เว็บไซต์", "เว็บแอป",
        ],
        [KnowledgeCategory.DataScience] = [
            "ข้อมูล", "วิเคราะห์", "วิชวลไลซ์", "แพนดาส", "ดาต้าเซต", "อีทีแอล",
            "ไปป์ไลน์", "แดชบอร์ด", "เมตริก", "เอสคิวแอล", "แวร์เฮาส์", "บิ๊กดาต้า",
        ],
        [KnowledgeCategory.Health_Medicine] = [
            "สุขภาพ", "การแพทย์", "วินิจฉัย", "รักษา", "อาการ", "โรค", "บำบัด",
            "คลินิก", "ผู้ป่วย", "ยา", "เภสัช", "แพทย์",
        ],
        [KnowledgeCategory.Philosophy] = [
            "ปรัชญา", "จริยศาสตร์", "จิตสำนึก", "การมีอยู่", "ตรรกะ", "อภิปรัชญา",
            "ญาณวิทยา", "ศีลธรรม", "จิตวิญญาณ",
        ],
        [KnowledgeCategory.GameDev] = [
            "เกม", "ยูนิตี", "อันเรียล", "สไปรต์", "เชเดอร์", "ฟิสิกส์เกม",
            "เกมเพลย์", "เลเวลดีไซน์", "มัลติเพลเยอร์", "เรนเดอริง", "พัฒนาเกม",
        ],
    };

    /// <summary>
    /// Grapheme-aware word count. For content that is ≥ 30% Thai, we
    /// count text elements (user-perceived characters) and divide by ~6
    /// — roughly the average length of a Thai "word" in characters.
    /// For Latin-dominant content, falls back to whitespace tokenization.
    /// </summary>
    public static int CountWords(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return 0;

        int thaiChars = 0;
        int latinChars = 0;
        foreach (var ch in content)
        {
            if (ch >= '\u0E00' && ch <= '\u0E7F') thaiChars++;
            else if (char.IsLetter(ch)) latinChars++;
        }
        var totalLetters = thaiChars + latinChars;
        if (totalLetters == 0) return 0;

        var thaiRatio = (double)thaiChars / totalLetters;

        if (thaiRatio < 0.3)
        {
            // Mostly Latin — ordinary whitespace split
            return content.Split([' ', '\n', '\r', '\t'],
                StringSplitOptions.RemoveEmptyEntries).Length;
        }

        // Mostly Thai — approximate words by dividing grapheme count
        // by an average Thai word length (~6 graphemes per word).
        // Also count Latin words separately and add them.
        var latinWords = CountLatinWords(content);
        var thaiGraphemes = CountThaiGraphemes(content);
        var estThaiWords = Math.Max(1, thaiGraphemes / 6);
        return latinWords + estThaiWords;
    }

    private static int CountLatinWords(string content)
    {
        // Count runs of Latin letters/digits separated by non-word chars
        int count = 0;
        bool inWord = false;
        foreach (var ch in content)
        {
            var isWord = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')
                || (ch >= '0' && ch <= '9');
            if (isWord && !inWord) { count++; inWord = true; }
            else if (!isWord) inWord = false;
        }
        return count;
    }

    private static int CountThaiGraphemes(string content)
    {
        int count = 0;
        var si = StringInfo.GetTextElementEnumerator(content);
        while (si.MoveNext())
        {
            var el = (string)si.Current;
            if (el.Length > 0 && el[0] >= '\u0E00' && el[0] <= '\u0E7F')
                count++;
        }
        return count;
    }

    /// <summary>
    /// Fraction of letters in content that are Thai (0..1).
    /// Lets us decide when to apply Thai-specific logic.
    /// </summary>
    public static double ThaiRatio(string content)
    {
        int thai = 0, total = 0;
        foreach (var ch in content)
        {
            if (char.IsLetter(ch))
            {
                total++;
                if (ch >= '\u0E00' && ch <= '\u0E7F') thai++;
            }
        }
        return total == 0 ? 0 : (double)thai / total;
    }
}
