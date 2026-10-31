import uno
import sys

host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
port = int(sys.argv[2]) if len(sys.argv) > 2 else 2002
local = uno.getComponentContext()
resolver = local.ServiceManager.createInstanceWithContext(
    "com.sun.star.bridge.UnoUrlResolver", local)
ctx = resolver.resolve(
    "uno:socket,host=%s,port=%d;urp;StarOffice.ComponentContext" % (host, port))
desktop = ctx.ServiceManager.createInstanceWithContext(
    "com.sun.star.frame.Desktop", ctx)
print("connected")
enum = desktop.Components.createEnumeration()
n = 0
while enum.hasMoreElements():
    c = enum.nextElement()
    n += 1
    try:
        url = c.getURL()
    except Exception as e:
        url = "<no url: %s>" % e
    try:
        pres = c.supportsService("com.sun.star.presentation.PresentationDocument")
    except Exception as e:
        pres = "<err %s>" % e
    try:
        title = c.getTitle()
    except Exception:
        title = ""
    print("component", n, "url=", url, "presentation=", pres, "title=", title)
print("total components", n)
