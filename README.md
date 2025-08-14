# Kit-Plus
Discord : https://discord.gg/cSD4A44nK4
راه‌اندازی سریع

فایل بالا را با نام KitsPlus.cs در oxide/plugins/ کپی کن.

داخل کنسول:

oxide.reload KitsPlus
oxide.grant group default kitsplus.use
oxide.grant group admin kitsplus.admin


(اختیاری) اگر Economics یا ServerRewards داری، آن‌ها را نصب/فعال کن. در صورت نیاز UseEconomics و UseServerRewards را در کانفیگ روشن کن.

دستورها

کاربری:

/kit → باز کردن UI

/kit claim <name> → دریافت کیت

/kit preview <name> → UI با فیلتر/پیش‌نمایش

/kit stats → آمار شخصی

ادمین:

/kit.admin add <name> → ساخت کیت از موجودی فعلی

/kit.admin set <kit> <field> <value> → تنظیم فیلد (نمونه‌ها پایین)

/kit.admin remove <name> | /kit.admin list | /kit.admin give <player> <kit> | /kit.admin ui

نمونهٔ فیلدها برای set:

displayname, description, permission, authlevel(0..2)
cooldown(مثل 30m/2h/1d), maxuses, onetime(true/false), resetonwipe(true/false)
daily(true/false), weekly(true/false)
randomize(true/false), rolls(>=1)
teamshared(true/false)
minlevel(مثلاً 5)  // نیازمند پلاگین LevelSystem (اختیاری)
category
cost.money, cost.rp
window.from (ISO) , window.to (ISO) , window.days (Saturday,Sunday)



کیت استارتر روزانه با کول‌داون ۳۰ دقیقه و یک‌بار در هر وایپ:

/kit.admin set starter daily true
/kit.admin set starter cooldown 30m
/kit.admin set starter onetime false
/kit.admin set starter resetonwipe true
oxide.grant group default kitsplus.kit.starter


کیت VIP پولی:

/kit.admin set vip cost.money 5000
/kit.admin set vip permission myserver.vip
oxide.grant group vip myserver.vip


کیت تیمی:

/kit.admin set raid teamshared true


کیت با پنجرهٔ زمانی آخر هفته:

/kit.admin set weekend window.days Saturday,Sunday


کیت رندومی با 3 رول:

/kit.admin set loot randomize true
/kit.admin set loot rolls 3

استفاده از قابلیت تیمی (۳/۴/>۵)

اسم کیت‌های ویژه تیم به‌صورت پیش‌فرض:

برای ۳ نفره: team3

برای ۴ نفره: team4

این کیت‌ها فقط زمانی در UI دیده و قابل دریافت‌اند که اندازهٔ تیم:

۳ ⇒ team3 فعال

۴ ⇒ team3 + team4 فعال

۵ ⇒ هر دو فعال

بیشتر از ۵ ⇒ هر دو حذف می‌شن (نه نمایش، نه دریافت)

پر کردن محتوا برای کیت‌های تیمی

ابتدا خودت رو با آیتم‌های مدنظر تجهیز کن و:

/kit.admin add team3
/kit.admin add team4


 ویژگی‌هاشون رو هم ست کن (نمونه):

/kit.admin set team3 description پکیج تیمی برای ۳ تا ۵ نفر
/kit.admin set team3 cooldown 1h
/kit.admin set team4 description پکیج قوی‌تر برای ۴ تا ۵ نفر
/kit.admin set team4 cooldown 2h


پرمیشن عمومی این دو کیت به‌صورت خودکار هم قابل استفاده‌ست: kitsplus.kit.team3 و kitsplus.kit.team4 (اگر لازم داشتی دقیق‌تر کنترل کنی).
